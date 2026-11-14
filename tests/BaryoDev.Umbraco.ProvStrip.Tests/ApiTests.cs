using System.Net;
using System.Reflection;
using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Tests;
using BaryoDev.Umbraco.ProvStrip.Api;
using BaryoDev.Umbraco.ProvStrip.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using System.Security.Claims;
using Umbraco.Cms.Core.Security.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

// Over HTTP: the routes exist and refuse anonymous callers. The actions are called directly, because the test site
// cannot create a signed-in API user yet (brain #11128).
[Collection(UmbracoCollection.Name)]
public class ApiTests(UmbracoSiteFixture site)
{
    const string Base = "/umbraco/management/api/v1/baryodev/provstrip/";

    ProvStripApiController Api(params object[] overrides)
    {
        // Allowed unless a test passes its own authorization; Umbraco's real media permission handler needs a signed-in user.
        if (!overrides.OfType<IAuthorizationService>().Any()) overrides = [.. overrides, new FakeAuthorization(allow: true)];
        var api = ActivatorUtilities.CreateInstance<ProvStripApiController>(site.Services, overrides);
        api.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = site.Services } };
        return api;
    }

    static T Body<T>(IActionResult result, int status)
    {
        var o = result.ShouldBeAssignableTo<ObjectResult>()!;
        (o.StatusCode ?? 200).ShouldBe(status);
        return o.Value.ShouldBeOfType<T>();
    }

    [Theory, InlineData("GET", "media/{0}"), InlineData("POST", "media/{0}/remove"), InlineData("POST", "media/{0}/skip"),
     InlineData("POST", "scan"), InlineData("GET", "scan"), InlineData("GET", "scan/results"), InlineData("POST", "scan/remove-all")]
    public async Task AnonymousGets401(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Base + string.Format(route, Guid.NewGuid()));
        if (method == "POST") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await site.AnonymousClient().SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact] public async Task AMissingRouteIsNot401()
    {
        // Proves the 401s above come from mapped routes, not from everything under the Management API path.
        using var response = await site.AnonymousClient().GetAsync(Base + "no-such-route");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The controller's own attribute; the base class adds Umbraco's back office policies on top.
    // The package registers its own assembly with MVC; a site whose build did not list it would otherwise 404.
    [Fact] public void ControllerRoutesAreMappedOnce()
    {
        var routes = site.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(d => d.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "")
            .Where(r => r.Contains("baryodev/provstrip", StringComparison.OrdinalIgnoreCase)).ToList();
        // Seven actions; a second copy of the assembly part would map each twice.
        routes.Count.ShouldBe(7);
    }

    [Fact] public async Task ItemEndpointsAskForThatItem()
    {
        var media = await site.UploadAsync("camera.jpg");
        var auth = new FakeAuthorization(allow: true);
        await Api(auth).GetMedia(media.Key);
        await Api(auth).Skip(media.Key, new SkipRequestModel { Category = "Camera", Hash = "x" });
        await Api(auth).Remove(media.Key, new RemoveRequestModel { Category = "Camera", Hash = "x" });
        auth.Asked.Count.ShouldBe(3);
        auth.Asked.ShouldAllBe(r => !r.CheckRoot && r.MediaKeys.Single() == media.Key);
        auth.Policies.ShouldAllBe(p => p == AuthorizationPolicies.MediaPermissionByResource);
    }

    [Fact] public async Task LibraryEndpointsNeedRootAccess()
    {
        var auth = new FakeAuthorization(allow: true);
        await Api(auth).GetScan();
        await Api(auth).GetResults(0, 10);
        await Api(auth, new RemoveAllQueue()).RemoveAll(new RemoveAllRequestModel());
        auth.Asked.Count.ShouldBe(3);
        auth.Asked.ShouldAllBe(r => r.CheckRoot);
    }

    [Fact] public async Task ADeniedUserGets403AndNothingChanges()
    {
        var media = await site.UploadAsync("camera.jpg");
        var before = await site.ReadMediaFileAsync(media);
        var deny = new FakeAuthorization(allow: false);
        var queue = new RemoveAllQueue();
        IActionResult[] results =
        [
            await Api(deny).GetMedia(media.Key),
            await Api(deny).Remove(media.Key, new RemoveRequestModel { Category = "Camera", Hash = (await site.Resolve<IFindingStore>().ForMediaAsync(media.Key))[0].ContentHash }),
            await Api(deny).Skip(media.Key, new SkipRequestModel { Category = "Camera", Hash = "x" }),
            await Api(deny).StartScan(),
            await Api(deny).GetScan(),
            await Api(deny).GetResults(0, 10),
            await Api(deny, queue).RemoveAll(new RemoveAllRequestModel()),
        ];
        results.ShouldAllBe(r => r is IStatusCodeActionResult && ((IStatusCodeActionResult)r).StatusCode == 403);
        (await site.ReadMediaFileAsync(media)).ShouldBe(before);
        queue.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact] public void EveryActionNeedsMediaAccess() =>
        typeof(ProvStripApiController).GetCustomAttributes<AuthorizeAttribute>(inherit: false).Select(a => a.Policy)
            .ShouldContain(AuthorizationPolicies.SectionAccessMedia);

    [Fact] public async Task RemoveThroughTheApiCleansTheFile()
    {
        var media = await site.UploadAsync("camera.jpg");
        var found = Body<MediaFindingsModel>(await Api().GetMedia(media.Key), 200);
        found.Labels.ShouldContain(l => l.StartsWith("Camera data found"));
        var after = Body<MediaFindingsModel>(await Api().Remove(media.Key, new RemoveRequestModel { Category = "Camera", Hash = found.Hash! }), 200);
        after.Labels.ShouldBeEmpty();
        ProvStripEngine.Inspect(await site.ReadMediaFileAsync(media)).Findings.ShouldNotContain(f => f.Category == Category.Camera);
    }

    [Fact] public async Task StaleHashGets409()
    {
        var media = await site.UploadAsync("camera.jpg");
        var before = await site.ReadMediaFileAsync(media);
        Body<ProblemDetails>(await Api().Remove(media.Key, new RemoveRequestModel { Category = "Camera", Hash = new string('0', 64) }), 409);
        (await site.ReadMediaFileAsync(media)).ShouldBe(before);
    }

    [Fact] public async Task UnknownCategoryGets400()
    {
        var media = await site.UploadAsync("camera.jpg");
        Body<ProblemDetails>(await Api().Remove(media.Key, new RemoveRequestModel { Category = "Unknown", Hash = "x" }), 400);
    }

    [Fact] public async Task MissingMediaGets404() =>
        Body<ProblemDetails>(await Api().GetMedia(Guid.NewGuid()), 404);

    [Fact] public async Task SkipHidesTheLabel()
    {
        var media = await site.UploadAsync("c2pa.jpg");
        var found = Body<MediaFindingsModel>(await Api().GetMedia(media.Key), 200);
        found.Labels.ShouldContain(l => l.StartsWith("AI attribution found"));
        var after = Body<MediaFindingsModel>(await Api().Skip(media.Key, new SkipRequestModel { Category = "Provenance", Hash = found.Hash! }), 200);
        after.Labels.ShouldNotContain(l => l.StartsWith("AI attribution found"));
    }

    [Fact] public async Task SecondScanGets409()
    {
        var scans = site.Resolve<IScanStore>();
        await FinishOpenScan(scans);
        try
        {
            Body<ScanModel>(await Api().StartScan(), 202).State.ShouldBe("Requested");
            Body<ProblemDetails>(await Api().StartScan(), 409);
            Body<ScanModel>(await Api().GetScan(), 200).State.ShouldBe("Requested");
        }
        finally
        {
            await FinishOpenScan(scans);
        }
    }

    static async Task FinishOpenScan(IScanStore scans)
    {
        if (await scans.CurrentAsync() is { State: ScanState.Requested or ScanState.Running } open) await scans.FinishAsync(open.Id);
    }

    [Fact] public async Task ResultsListItemsWithOpenFindings()
    {
        var media = await site.UploadAsync("c2pa.png");
        var page = Body<PagedResultsModel>(await Api().GetResults(0, 500), 200);
        page.Items.ShouldContain(i => i.Key == media.Key && i.Labels.Any(l => l.StartsWith("AI attribution found")));
    }

    [Fact] public async Task RemoveAllQueuesAndTheWorkerCleansEveryOpenItem()
    {
        var first = await site.UploadAsync("camera.jpg");
        var second = await site.UploadAsync("camera.png");
        // Its own queue, so the site's background worker does not run the same removal alongside this one.
        var queue = new RemoveAllQueue();
        (await Api(queue).RemoveAll(new RemoveAllRequestModel { Category = "Camera" })).ShouldBeOfType<AcceptedResult>();
        queue.Reader.TryRead(out var queued).ShouldBeTrue();
        queued.ShouldBe(Category.Camera);
        await site.Resolve<RemoveAllWorker>().RunAsync(Category.Camera, default);
        foreach (var media in new[] { first, second })
            ProvStripEngine.Inspect(await site.ReadMediaFileAsync(media)).Findings.ShouldNotContain(f => f.Category == Category.Camera);
    }
}

/// <summary>Allows or denies every check, and records the media permission resources and policies it was asked about.</summary>
sealed class FakeAuthorization(bool allow) : IAuthorizationService
{
    public List<MediaPermissionResource> Asked { get; } = [];
    public List<string> Policies { get; } = [];

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Result(resource, "");

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) => Result(resource, policyName);

    Task<AuthorizationResult> Result(object? resource, string policy)
    {
        if (resource is MediaPermissionResource media) Asked.Add(media);
        Policies.Add(policy);
        return Task.FromResult(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}

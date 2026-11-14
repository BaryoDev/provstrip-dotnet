using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Tests;
using BaryoDev.Umbraco.ProvStrip.Api;
using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

// Findings from the final branch review, each pinned before its fix.
[Collection(UmbracoCollection.Name)]
public class ReviewFixTests(UmbracoSiteFixture site)
{
    IMediaProvStrip Strip => site.Resolve<IMediaProvStrip>();
    IFindingStore Findings => site.Resolve<IFindingStore>();

    ProvStripApiController Api()
    {
        var api = ActivatorUtilities.CreateInstance<ProvStripApiController>(site.Services, new FakeAuthorization(allow: true));
        api.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = site.Services } };
        return api;
    }

    static T Body<T>(IActionResult result, int status)
    {
        var o = result.ShouldBeAssignableTo<ObjectResult>()!;
        (o.StatusCode ?? 200).ShouldBe(status);
        return o.Value.ShouldBeOfType<T>();
    }

    async Task<string> HashOf(IMedia media) => (await Findings.ForMediaAsync(media.Key))[0].ContentHash;

    async Task<Inspection> StoredAsync(IMedia media) => ProvStripEngine.Inspect(await site.ReadMediaFileAsync(media));

    [Fact] public async Task RemoveWithoutACategoryKeepsASkippedCategory()
    {
        var media = await site.UploadAsync(UmbracoSiteFixture.MixedJpeg(), "mixed.jpg");
        var hash = await HashOf(media);
        await Findings.SkipAsync(media.Key, hash, Category.Provenance);
        try
        {
            await Strip.RemoveAsync(media.Key, null, hash, default);
            var stored = await StoredAsync(media);
            stored.Findings.ShouldContain(f => f.Category == Category.Provenance);
            stored.Findings.ShouldNotContain(f => f.Category == Category.Camera);
        }
        finally
        {
            await Findings.DeleteForMediaAsync(media.Key);
        }
    }

    // Deleting camera data changes the file's hash; the skip on its AI attribution must come along.
    [Fact] public async Task ASkipOutlivesADeleteOfTheOtherCategory()
    {
        var media = await site.UploadAsync(UmbracoSiteFixture.MixedJpeg(), "mixed.jpg");
        var hash = await HashOf(media);
        await Findings.SkipAsync(media.Key, hash, Category.Provenance);
        try
        {
            await Strip.RemoveAsync(media.Key, Category.Camera, hash, default);
            var rows = await Findings.ForMediaAsync(media.Key);
            rows.ShouldContain(f => f.Category == Category.Provenance && f.Status == FindingStatus.Skipped);
            rows.ShouldNotContain(f => f.Category == Category.Provenance && f.Status == FindingStatus.Open);
        }
        finally
        {
            await Findings.DeleteForMediaAsync(media.Key);
        }
    }

    [Fact] public async Task DeleteAllWithoutACategoryKeepsSkips()
    {
        var media = await site.UploadAsync(UmbracoSiteFixture.MixedJpeg(), "mixed.jpg");
        await Findings.SkipAsync(media.Key, await HashOf(media), Category.Provenance);
        try
        {
            await site.Resolve<RemoveAllWorker>().RunAsync(null, default);
            var stored = await StoredAsync(media);
            stored.Findings.ShouldContain(f => f.Category == Category.Provenance);
            stored.Findings.ShouldNotContain(f => f.Category == Category.Camera);
        }
        finally
        {
            // A skip is stored against the file's hash; leave none behind for other tests' copies of the fixture.
            await Findings.DeleteForMediaAsync(media.Key);
        }
    }

    [Fact] public async Task AStaleDeleteRecordsTheFileAsItIsNow()
    {
        var media = await site.UploadAsync("camera.jpg");
        var shown = await HashOf(media);
        // Replaced outside Umbraco after the check.
        using (var clean = new MemoryStream(Fixtures.Read("clean.jpg")))
            site.Resolve<MediaFileManager>().FileSystem.AddFile(site.RelativePathOf(media), clean, overrideIfExists: true);
        await Should.ThrowAsync<StaleFileException>(() => Strip.RemoveAsync(media.Key, Category.Camera, shown, default));
        (await Findings.ForMediaAsync(media.Key)).ShouldNotContain(f => f.ContentHash == shown);
    }

    [Fact] public async Task ResultsCarryEachItemsHash()
    {
        var media = await site.UploadAsync("c2pa.png");
        var page = Body<PagedResultsModel>(await Api().GetResults(0, 500), 200);
        page.Items.Single(i => i.Key == media.Key).Hash.ShouldBe(await HashOf(media));
    }

    [Fact] public async Task OneFailingItemDoesNotStopDeleteAll()
    {
        await site.UploadAsync("camera.jpg");
        var next = await site.UploadAsync("camera.png");
        // Fails every item but one, so wherever that one sorts, an earlier failure would have stopped the run.
        var strip = new FailingFor(next.Key, Strip);
        var worker = ActivatorUtilities.CreateInstance<RemoveAllWorker>(site.Services, strip);
        await worker.RunAsync(Category.Camera, default);
        (await StoredAsync(next)).Findings.ShouldNotContain(f => f.Category == Category.Camera);
    }

    [Fact] public async Task DeleteWorksOnAMediaTypeWithoutASizeProperty()
    {
        var types = site.Resolve<IMediaTypeService>();
        const string alias = "provstripImageNoBytes";
        if (types.Get(alias) is null)
        {
            var copy = (IMediaType)types.Get(Constants.Conventions.MediaTypes.Image)!.DeepCloneWithResetIdentities(alias);
            copy.Name = "Image without size";
            copy.RemovePropertyType(Constants.Conventions.Media.Bytes);
            var created = await types.CreateAsync(copy, Constants.Security.SuperUserKey);
            created.Success.ShouldBeTrue(created.Result.ToString());
        }
        var media = await site.UploadAsync(Fixtures.Read("camera.jpg"), "camera.jpg", alias);
        await Strip.RemoveAsync(media.Key, Category.Camera, await HashOf(media), default);
        (await StoredAsync(media)).Findings.ShouldNotContain(f => f.Category == Category.Camera);
        (await Findings.ForMediaAsync(media.Key)).ShouldContain(f => f.Category == Category.Camera && f.Status == FindingStatus.Removed);
    }

    [Fact] public async Task TrashedMediaLeavesTheResults()
    {
        var media = await site.UploadAsync("c2pa.png");
        site.Resolve<IMediaService>().MoveToRecycleBin(media);
        (await Findings.ForMediaAsync(media.Key)).ShouldBeEmpty();
    }

    [Fact] public async Task DeletedMediaLeavesTheResults()
    {
        var media = await site.UploadAsync("c2pa.png");
        site.Resolve<IMediaService>().Delete(media);
        (await Findings.ForMediaAsync(media.Key)).ShouldBeEmpty();
    }

    [Fact] public async Task TheItemSaysWhatWasRemoved()
    {
        var media = await site.UploadAsync("camera.jpg");
        await Strip.RemoveAsync(media.Key, Category.Camera, await HashOf(media), default);
        Body<MediaFindingsModel>(await Api().GetMedia(media.Key), 200).Removed
            .ShouldContain(r => r.StartsWith("Camera data removed ("));
    }

    [Fact] public void RemovedLabelsNameTheCategoryAndWhat() =>
        Labels.Removed([new FindingRecord(Guid.Empty, "", Category.Camera, "r", "GPS", FindingStatus.Removed),
                        new FindingRecord(Guid.Empty, "", Category.Provenance, "r", "C2PA", FindingStatus.Removed)])
            .ShouldBe(["AI attribution removed (C2PA)", "Camera data removed (GPS)"]);

    [Fact] public async Task AnUnknownSegmentCanBeSkipped()
    {
        var media = await site.UploadAsync("unknown-ancillary.png");
        var found = Body<MediaFindingsModel>(await Api().GetMedia(media.Key), 200);
        found.NeedsLook.ShouldNotBeNull();
        try
        {
            var after = Body<MediaFindingsModel>(await Api().Skip(media.Key, new SkipRequestModel { Category = "Unknown", Hash = found.Hash! }), 200);
            after.NeedsLook.ShouldBeNull();
            (await Findings.OpenAsync(0, 100_000)).Items.ShouldNotContain(i => i.MediaKey == media.Key);
        }
        finally
        {
            await Findings.DeleteForMediaAsync(media.Key);
        }
    }

    [Fact] public async Task ANumberIsNotACategory()
    {
        var media = await site.UploadAsync("camera.jpg");
        Body<ProblemDetails>(await Api().Skip(media.Key, new SkipRequestModel { Category = "99", Hash = await HashOf(media) }), 400);
    }

    /// <summary>Fails every removal except the one for <c>key</c>.</summary>
    sealed class FailingFor(Guid key, IMediaProvStrip inner) : IMediaProvStrip
    {
        public Task<MediaCheck> CheckAsync(IMedia media, CancellationToken ct) => inner.CheckAsync(media, ct);

        public Task<MediaCheck> RemoveAsync(Guid mediaKey, Category? category, string expectedHash, CancellationToken ct) =>
            mediaKey != key ? throw new InvalidOperationException("storage refused the write") : inner.RemoveAsync(mediaKey, category, expectedHash, ct);
    }
}

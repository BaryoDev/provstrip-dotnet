using Asp.Versioning;
using BaryoDev.ProvStrip;
using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.ProvStrip.Api;

/// <summary>
/// Checks, removals and skips for media items the user may access, and library scans for users whose media access
/// starts at the root. Media start nodes are honoured the same way Umbraco's own media endpoints honour them.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("baryodev/provstrip")]
[ApiExplorerSettings(GroupName = "ProvStrip")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessMedia)]
public sealed class ProvStripApiController(
    IMediaProvStrip strip,
    IFindingStore findings,
    IScanStore scans,
    RemoveAllQueue removeAll,
    IMediaService mediaService,
    IIdKeyMap idKeyMap,
    IAuthorizationService authorization) : ManagementApiControllerBase
{
    [HttpGet("media/{key:guid}")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(MediaFindingsModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMedia(Guid key)
    {
        if (!await MayUse(MediaPermissionResource.WithKeys(key))) return Forbidden();
        if (Find(key) is not { } media) return NotFoundProblem(key);
        var rows = await findings.ForMediaAsync(key);
        // Media added before the package was installed has no rows until the scan reaches it; check it now.
        if (rows.Count == 0) return Ok(ToModel(await strip.CheckAsync(media, HttpContext.RequestAborted)));
        return Ok(ToModel(key, rows));
    }

    [HttpPost("media/{key:guid}/remove")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(MediaFindingsModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Remove(Guid key, RemoveRequestModel request)
    {
        if (!await MayUse(MediaPermissionResource.WithKeys(key))) return Forbidden();
        if (!TryCategory(request.Category, out var category)) return CategoryProblem(request.Category);
        try
        {
            return Ok(ToModel(await strip.RemoveAsync(key, category, request.Hash, HttpContext.RequestAborted)));
        }
        catch (StaleFileException e)
        {
            return ProblemResult(StatusCodes.Status409Conflict, "File changed", e.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundProblem(key);
        }
    }

    [HttpPost("media/{key:guid}/skip")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(MediaFindingsModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Skip(Guid key, SkipRequestModel request)
    {
        if (!await MayUse(MediaPermissionResource.WithKeys(key))) return Forbidden();
        if (!TryCategory(request.Category, out var category) || category is null) return CategoryProblem(request.Category);
        if (Find(key) is null) return NotFoundProblem(key);
        // A skip is stored against a file hash, so only this item's current hash may be skipped.
        if ((await findings.ForMediaAsync(key)).FirstOrDefault()?.ContentHash != request.Hash)
            return ProblemResult(StatusCodes.Status409Conflict, "File changed", "The hash is not this item's current file. Check it again.");
        await findings.SkipAsync(key, request.Hash, category.Value);
        return Ok(ToModel(key, await findings.ForMediaAsync(key)));
    }

    [HttpPost("scan")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(ScanModel), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartScan()
    {
        if (!await MayUse(MediaPermissionResource.Root())) return Forbidden();
        try
        {
            return StatusCode(StatusCodes.Status202Accepted, ToModel(await scans.RequestAsync()));
        }
        catch (ScanRunningException e)
        {
            return ProblemResult(StatusCodes.Status409Conflict, "Scan running", e.Message);
        }
    }

    [HttpGet("scan")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(ScanModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetScan()
    {
        if (!await MayUse(MediaPermissionResource.Root())) return Forbidden();
        return Ok(await scans.CurrentAsync() is { } scan ? ToModel(scan) : new ScanModel("None", 0, 0, 0, null, null));
    }

    [HttpGet("scan/results")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(PagedResultsModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetResults(int skip = 0, int take = 50)
    {
        if (!await MayUse(MediaPermissionResource.Root())) return Forbidden();
        if (skip < 0 || take is < 1 or > 500) return ProblemResult(StatusCodes.Status400BadRequest, "Bad paging", "skip must be 0 or more and take 1 to 500.");
        var page = await findings.OpenAsync(skip, take);
        var folders = new Dictionary<int, string>();
        var items = new List<MediaFindingsSummaryModel>(page.Items.Count);
        foreach (var item in page.Items)
        {
            var media = Find(item.MediaKey);
            var needsLook = item.Findings.FirstOrDefault(f => f.Status == FindingStatus.NeedsLook)?.Description;
            items.Add(new MediaFindingsSummaryModel(item.MediaKey, media?.Name ?? "", media is null ? "" : FolderOf(media, folders),
                Labels.For(item.Findings), needsLook));
        }
        return Ok(new PagedResultsModel(page.Total, items));
    }

    [HttpPost("scan/remove-all")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveAll(RemoveAllRequestModel request)
    {
        if (!await MayUse(MediaPermissionResource.Root())) return Forbidden();
        if (!TryCategory(request.Category, out var category)) return CategoryProblem(request.Category);
        removeAll.Enqueue(category);
        return Accepted();
    }

    async Task<bool> MayUse(MediaPermissionResource resource) =>
        (await authorization.AuthorizeResourceAsync(User, resource, AuthorizationPolicies.MediaPermissionByResource)).Succeeded;

    // By id: IMediaService.GetById(Guid) is binary-incompatible between Umbraco 17 and 18, GetById(int) is not.
    IMedia? Find(Guid key)
    {
        var id = idKeyMap.GetIdForKey(key, UmbracoObjectTypes.Media);
        return id.Success ? mediaService.GetById(id.Result) : null;
    }

    /// <summary>Folder names above the item, from the root: "Blog/2026". Cached per request by folder id.</summary>
    string FolderOf(IMedia media, Dictionary<int, string> cache)
    {
        if (media.ParentId <= 0) return "";
        if (cache.TryGetValue(media.ParentId, out var known)) return known;
        var parent = mediaService.GetById(media.ParentId);
        var path = parent is null ? "" : (FolderOf(parent, cache) is { Length: > 0 } above ? $"{above}/{parent.Name}" : parent.Name ?? "");
        cache[media.ParentId] = path;
        return path;
    }

    static bool TryCategory(string? value, out Category? category)
    {
        category = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (Enum.TryParse<Category>(value, ignoreCase: true, out var parsed) && parsed != Category.Unknown)
        {
            category = parsed;
            return true;
        }
        return false;
    }

    IActionResult CategoryProblem(string? value) =>
        ProblemResult(StatusCodes.Status400BadRequest, "Unknown category", $"'{value}' is not a category. Use Provenance or Camera.");

    IActionResult NotFoundProblem(Guid key) => ProblemResult(StatusCodes.Status404NotFound, "Media not found", $"No media item {key}.");

    // A plain ProblemDetails, so no problem details factory is needed and the shape is the same on 17 and 18.
    static ObjectResult ProblemResult(int status, string title, string detail) =>
        new(new ProblemDetails { Status = status, Title = title, Detail = detail }) { StatusCode = status };

    static MediaFindingsModel ToModel(MediaCheck check) => new(check.MediaKey, check.Hash, Labels.For(check.Findings), check.Findings.Select(ToModel).ToList(), check.NeedsLook);

    static MediaFindingsModel ToModel(Guid key, IReadOnlyList<FindingRecord> rows) =>
        new(key, rows.FirstOrDefault()?.ContentHash, Labels.For(rows), rows.Select(ToModel).ToList(),
            rows.FirstOrDefault(f => f.Status == FindingStatus.NeedsLook)?.Description);

    static FindingModel ToModel(FindingRecord f) => new(f.Category.ToString(), f.RuleId, f.Description, f.Status.ToString());

    static ScanModel ToModel(ScanRecord s) => new(s.State.ToString(), s.Total, s.Checked, s.WithFindings, s.StartedUtc, s.FinishedUtc);
}

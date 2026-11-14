using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Persistence.Dtos;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.ProvStrip.Scan;

/// <summary>
/// Moves a requested library scan forward: every 5 seconds it checks the next <see cref="ProvStripOptions.ScanBatchSize"/>
/// media items after the stored cursor, in id order, and stores the new cursor. Reads one page at a time, never the
/// whole library, and picks up where it stopped after a restart. Runs on the scheduling server only.
/// </summary>
public sealed class ProvStripScanJob(
    IScanStore scans,
    IMediaProvStrip strip,
    IMediaService mediaService,
    IScopeProvider scopes,
    IOptions<ProvStripOptions> options,
    ILogger<ProvStripScanJob> logger) : RecurringBackgroundJobBase(TimeSpan.FromSeconds(5))
{
    public override ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    public override async Task RunJobAsync(CancellationToken cancellationToken)
    {
        if (await scans.CurrentAsync() is not { State: ScanState.Requested or ScanState.Running } scan) return;
        if (scan.State == ScanState.Requested) await scans.StartAsync(scan.Id, CountMedia());

        var size = Math.Max(1, options.Value.ScanBatchSize);
        // One more than a batch, so the run that checks the last items also finishes the scan.
        var ids = NextIds(scan.CursorId, size + 1);
        var batch = ids.Take(size).ToList();
        var withFindings = 0;
        foreach (var id in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mediaService.GetById(id) is not { } media) continue;
            try
            {
                var check = await strip.CheckAsync(media, cancellationToken);
                if (check.NeedsLook is not null || check.Findings.Any(f => f.Status == FindingStatus.Open)) withFindings++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One bad item must not stop the scan; the check itself records file problems as NeedsLook.
                logger.LogError(e, "ProvStrip could not check media {MediaId} during a scan", id);
            }
        }

        if (batch.Count > 0) await scans.AdvanceAsync(scan.Id, batch[^1], batch.Count, withFindings);
        if (ids.Count <= size) await scans.FinishAsync(scan.Id);
    }

    int CountMedia()
    {
        using var scope = scopes.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql().SelectCount().From<NodeDto>()
            .Where<NodeDto>(x => x.NodeObjectType == Constants.ObjectTypes.Media && !x.Trashed);
        return scope.Database.ExecuteScalar<int>(sql);
    }

    List<int> NextIds(int afterId, int take)
    {
        using var scope = scopes.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql().Select<NodeDto>(x => x.NodeId).From<NodeDto>()
            .Where<NodeDto>(x => x.NodeObjectType == Constants.ObjectTypes.Media && !x.Trashed && x.NodeId > afterId)
            .OrderBy<NodeDto>(x => x.NodeId);
        return scope.Database.SkipTake<int>(0, take, sql);
    }
}

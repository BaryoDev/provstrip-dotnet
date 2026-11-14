using System.Collections.Concurrent;
using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Tests;
using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using BaryoDev.Umbraco.ProvStrip.Scan;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

// The shared site holds media from other tests too, so counts are checked against the scan's own Total.
[Collection(UmbracoCollection.Name)]
public class ScanTests(UmbracoSiteFixture site)
{
    IFindingStore Findings => site.Resolve<IFindingStore>();
    IScanStore Scans => site.Resolve<IScanStore>();

    ProvStripScanJob Job(int batchSize = 50, IMediaProvStrip? strip = null)
    {
        var options = Options.Create(new ProvStripOptions { ScanBatchSize = batchSize });
        return strip is null
            ? ActivatorUtilities.CreateInstance<ProvStripScanJob>(site.Services, options)
            : ActivatorUtilities.CreateInstance<ProvStripScanJob>(site.Services, options, strip);
    }

    /// <summary>Requests a scan (finishing any left open) and runs the job until it is Done; returns Checked after each run.</summary>
    async Task<List<int>> ScanToDoneAsync(ProvStripScanJob job, int maxRuns = 500)
    {
        if (await Scans.CurrentAsync() is { State: ScanState.Requested or ScanState.Running } open) await Scans.FinishAsync(open.Id);
        await Scans.RequestAsync();
        return await RunToDoneAsync(job, maxRuns);
    }

    async Task<List<int>> RunToDoneAsync(ProvStripScanJob job, int maxRuns = 500)
    {
        var progress = new List<int>();
        for (var run = 0; run < maxRuns; run++)
        {
            await job.RunJobAsync(default);
            var scan = (await Scans.CurrentAsync())!;
            progress.Add(scan.Checked);
            if (scan.State == ScanState.Done) return progress;
        }
        throw new InvalidOperationException($"scan not done after {maxRuns} runs");
    }

    /// <summary>Uploads, then forgets what the upload check recorded, so only a scan can record it again.</summary>
    async Task<IMedia> UploadUncheckedAsync(string fixture)
    {
        var media = await site.UploadAsync(fixture);
        foreach (var hash in (await Findings.ForMediaAsync(media.Key)).Select(f => f.ContentHash).Distinct())
            await Findings.ReplaceAsync(media.Key, hash, []);
        return media;
    }

    async Task<bool> InResultsAsync(Guid key) => (await Findings.OpenAsync(0, 100_000)).Items.Any(i => i.MediaKey == key);

    [Fact] public async Task ScanFindsAndChangesNothing()
    {
        var c2pa = await UploadUncheckedAsync("c2pa.jpg");
        var camera = await UploadUncheckedAsync("camera.jpg");
        var clean = await UploadUncheckedAsync("clean.png");
        var before = new[] { await site.ReadMediaFileAsync(c2pa), await site.ReadMediaFileAsync(camera), await site.ReadMediaFileAsync(clean) };

        await ScanToDoneAsync(Job());

        (await InResultsAsync(c2pa.Key)).ShouldBeTrue();
        (await InResultsAsync(camera.Key)).ShouldBeTrue();
        (await InResultsAsync(clean.Key)).ShouldBeFalse();
        (await site.ReadMediaFileAsync(c2pa)).ShouldBe(before[0]);
        (await site.ReadMediaFileAsync(camera)).ShouldBe(before[1]);
        (await site.ReadMediaFileAsync(clean)).ShouldBe(before[2]);
    }

    [Fact] public async Task ScanReadsInBatches()
    {
        for (var i = 0; i < 5; i++) await UploadUncheckedAsync("camera.jpg");
        var progress = await ScanToDoneAsync(Job(batchSize: 2));
        var total = (await Scans.CurrentAsync())!.Total;
        total.ShouldBeGreaterThanOrEqualTo(5);
        // Two more per run until the last run, which checks what is left.
        progress.ShouldBe(Enumerable.Range(1, (total + 1) / 2).Select(run => Math.Min(run * 2, total)).ToList());
    }

    [Fact] public async Task ScanResumesFromItsCursor()
    {
        for (var i = 0; i < 3; i++) await UploadUncheckedAsync("camera.png");
        var checkedKeys = new ConcurrentDictionary<Guid, int>();
        var counting = new CountingStrip(site.Resolve<IMediaProvStrip>(), checkedKeys);

        if (await Scans.CurrentAsync() is { State: ScanState.Requested or ScanState.Running } open) await Scans.FinishAsync(open.Id);
        await Scans.RequestAsync();
        await Job(batchSize: 2, strip: counting).RunJobAsync(default);
        // A restart: a new job instance, picking up the stored cursor.
        await RunToDoneAsync(Job(batchSize: 2, strip: counting));

        checkedKeys.Values.ShouldAllBe(count => count == 1);
        checkedKeys.Count.ShouldBe((await Scans.CurrentAsync())!.Total);
    }

    [Fact] public async Task ScanSurvivesAMissingFile()
    {
        var media = await UploadUncheckedAsync("camera.jpg");
        site.Resolve<MediaFileManager>().FileSystem.DeleteFile(site.RelativePathOf(media));
        await ScanToDoneAsync(Job());
        (await Findings.ForMediaAsync(media.Key)).ShouldContain(f => f.Status == FindingStatus.NeedsLook && f.Description == "file not found");
    }

    [Fact] public async Task ScanSurvivesAnUnknownSegment()
    {
        var media = await UploadUncheckedAsync("unknown-ancillary.png");
        await ScanToDoneAsync(Job());
        (await Findings.ForMediaAsync(media.Key)).ShouldContain(f => f.Category == Category.Unknown && f.Status == FindingStatus.NeedsLook);
    }

    [Fact] public async Task ARunWithNoScanRequestedDoesNothing()
    {
        if (await Scans.CurrentAsync() is { State: ScanState.Requested or ScanState.Running } open) await Scans.FinishAsync(open.Id);
        var before = await Scans.CurrentAsync();
        await Job().RunJobAsync(default);
        (await Scans.CurrentAsync()).ShouldBe(before);
    }

    [Fact] public void JobRunsOnlyOnTheSchedulingServer() =>
        Job().ServerRoles.ShouldBe([ServerRole.Single, ServerRole.SchedulingPublisher], ignoreOrder: true);

    sealed class CountingStrip(IMediaProvStrip inner, ConcurrentDictionary<Guid, int> counts) : IMediaProvStrip
    {
        public Task<MediaCheck> CheckAsync(IMedia media, CancellationToken ct)
        {
            counts.AddOrUpdate(media.Key, 1, (_, n) => n + 1);
            return inner.CheckAsync(media, ct);
        }

        public Task<MediaCheck> RemoveAsync(Guid mediaKey, Category? category, string expectedHash, CancellationToken ct) =>
            inner.RemoveAsync(mediaKey, category, expectedHash, ct);
    }
}

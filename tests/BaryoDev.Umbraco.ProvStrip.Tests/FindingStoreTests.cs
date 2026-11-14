using BaryoDev.ProvStrip;
using BaryoDev.Umbraco.ProvStrip.Data;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

[Collection(UmbracoCollection.Name)]
public class FindingStoreTests(UmbracoSiteFixture site)
{
    static readonly string HashA = new('a', 64);
    static readonly string HashB = new('b', 64);

    IFindingStore Store => site.Resolve<IFindingStore>();

    static FindingRecord Open(Guid key, string hash, Category category) =>
        new(key, hash, category, category == Category.Camera ? "jpeg-exif" : "jpeg-c2pa", category == Category.Camera ? "GPS" : "C2PA", FindingStatus.Open);

    [Fact] public async Task SkipSurvivesARecheckOfTheSameFile()
    {
        var key = Guid.NewGuid();
        await Store.ReplaceAsync(key, HashA, [Open(key, HashA, Category.Provenance)]);
        await Store.SkipAsync(key, HashA, Category.Provenance);
        await Store.ReplaceAsync(key, HashA, [Open(key, HashA, Category.Provenance)]);
        (await Store.ForMediaAsync(key)).ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Skipped);
        (await Store.IsSkippedAsync(HashA, Category.Provenance)).ShouldBeTrue();
    }

    [Fact] public async Task ANewHashIsCheckedAgain()
    {
        var key = Guid.NewGuid();
        await Store.ReplaceAsync(key, HashA, [Open(key, HashA, Category.Provenance)]);
        await Store.SkipAsync(key, HashA, Category.Provenance);
        await Store.ReplaceAsync(key, HashB, [Open(key, HashB, Category.Provenance)]);
        (await Store.ForMediaAsync(key)).ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Open);
    }

    [Fact] public async Task SkipIsPerCategory()
    {
        var key = Guid.NewGuid();
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await Store.ReplaceAsync(key, hash, [Open(key, hash, Category.Provenance), Open(key, hash, Category.Camera)]);
        await Store.SkipAsync(key, hash, Category.Provenance);
        var rows = await Store.ForMediaAsync(key);
        rows.Single(r => r.Category == Category.Provenance).Status.ShouldBe(FindingStatus.Skipped);
        rows.Single(r => r.Category == Category.Camera).Status.ShouldBe(FindingStatus.Open);
    }

    [Fact] public async Task OpenListsOnlyItemsWithSomethingToDo()
    {
        Guid open = Guid.NewGuid(), skipped = Guid.NewGuid();
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await Store.ReplaceAsync(open, hash, [Open(open, hash, Category.Camera)]);
        var other = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await Store.ReplaceAsync(skipped, other, [Open(skipped, other, Category.Camera)]);
        await Store.SkipAsync(skipped, other, Category.Camera);
        var page = await Store.OpenAsync(0, 1000);
        page.Items.ShouldContain(i => i.MediaKey == open);
        page.Items.ShouldNotContain(i => i.MediaKey == skipped);
    }

    [Fact] public void TablesExistAfterInstall()
    {
        using var scope = site.Resolve<IScopeProvider>().CreateScope(autoComplete: true);
        scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM baryoDevProvStripFinding").ShouldBeGreaterThanOrEqualTo(0);
        scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM baryoDevProvStripScan").ShouldBeGreaterThanOrEqualTo(0);
    }
}

[Collection(UmbracoCollection.Name)]
public class ScanStoreTests(UmbracoSiteFixture site)
{
    [Fact] public async Task SecondScanRequestWhileRunningThrows()
    {
        var store = site.Resolve<IScanStore>();
        var scan = await store.RequestAsync();
        await store.StartAsync(scan.Id, total: 3);
        await Should.ThrowAsync<ScanRunningException>(store.RequestAsync());
        await store.FinishAsync(scan.Id);
        (await store.CurrentAsync())!.State.ShouldBe(ScanState.Done);
    }

    [Fact] public async Task AdvanceMovesTheCursorAndCounts()
    {
        var store = site.Resolve<IScanStore>();
        if ((await store.CurrentAsync()) is { State: ScanState.Requested or ScanState.Running } running) await store.FinishAsync(running.Id);
        var scan = await store.RequestAsync();
        await store.StartAsync(scan.Id, total: 5);
        await store.AdvanceAsync(scan.Id, cursorId: 42, checkedDelta: 2, withFindingsDelta: 1);
        var now = (await store.CurrentAsync())!;
        (now.State, now.CursorId, now.Total, now.Checked, now.WithFindings).ShouldBe((ScanState.Running, 42, 5, 2, 1));
        await store.FinishAsync(scan.Id);
    }
}

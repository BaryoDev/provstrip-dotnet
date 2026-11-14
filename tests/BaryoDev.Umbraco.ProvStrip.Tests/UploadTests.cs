using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Tests;
using BaryoDev.Umbraco.ProvStrip.Data;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

[Collection(UmbracoCollection.Name)]
public class UploadTests(UmbracoSiteFixture site)
{
    IFindingStore Store => site.Resolve<IFindingStore>();

    // Pins when the file is readable. Umbraco moves the temporary file into media storage in the property
    // value editor, before the save; the handler depends on the stored file existing at MediaSaving.
    [Fact] public async Task StoredFileIsReadableAtMediaSaving()
    {
        var media = await site.UploadAsBackofficeAsync("clean.png");
        site.Resolve<MediaSavingProbe>().FileReadable[media.Key].ShouldBeTrue();
    }

    [Fact] public async Task FlagUploadIsLabelledAndUnchanged()
    {
        var media = await site.UploadAsBackofficeAsync("c2pa.png");
        (await site.ReadMediaFileAsync(media)).ShouldBe(Fixtures.Read("c2pa.png"));
        (await Store.ForMediaAsync(media.Key)).ShouldContain(f => f.Category == Category.Provenance && f.Status == FindingStatus.Open);
    }

    [Fact] public async Task UnknownDoesNotBlockTheUpload()
    {
        var media = await site.UploadAsBackofficeAsync("unknown-ancillary.png");
        (await site.ReadMediaFileAsync(media)).ShouldBe(Fixtures.Read("unknown-ancillary.png"));
        (await Store.ForMediaAsync(media.Key)).ShouldContain(f => f.Category == Category.Unknown && f.Status == FindingStatus.NeedsLook);
    }

    [Fact] public async Task SavingWithoutANewFileDoesNotRecheck()
    {
        var media = await site.UploadAsync("camera.jpg");
        var findings = await Store.ForMediaAsync(media.Key);
        findings.ShouldNotBeEmpty();
        // Clear what the upload recorded. A recheck on the rename would record it again.
        await Store.ReplaceAsync(media.Key, findings[0].ContentHash, []);
        media.Name = "renamed";
        site.Resolve<IMediaService>().Save(media);
        (await Store.ForMediaAsync(media.Key)).ShouldBeEmpty();
    }
}

[Collection(AutoCameraCollection.Name)]
public class AutoUploadTests(AutoCameraSiteFixture site)
{
    [Fact] public async Task AutoCameraIsStrippedBeforeTheSaveEnds()
    {
        var media = await site.UploadAsBackofficeAsync("camera.jpg");
        var stored = await site.ReadMediaFileAsync(media);
        ProvStripEngine.Inspect(stored).Findings.ShouldNotContain(f => f.Category == Category.Camera);
        site.Reload(media).GetValue<long>(Constants.Conventions.Media.Bytes).ShouldBe(stored.Length);
        (await site.Resolve<IFindingStore>().ForMediaAsync(media.Key)).ShouldContain(f => f.Category == Category.Camera && f.Status == FindingStatus.Removed);
    }

    [Fact] public async Task AutoLeavesFlaggedCategoriesInTheFile()
    {
        var media = await site.UploadAsync(UmbracoSiteFixture.MixedJpeg(), "mixed.jpg");
        var inspection = ProvStripEngine.Inspect(await site.ReadMediaFileAsync(media));
        inspection.Findings.ShouldNotContain(f => f.Category == Category.Camera);
        inspection.Findings.ShouldContain(f => f.Category == Category.Provenance);
    }
}

using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

[Collection(UmbracoCollection.Name)]
public class MediaProvStripTests(UmbracoSiteFixture site)
{
    IMediaProvStrip Sut => site.Resolve<IMediaProvStrip>();

    static FindingRecord Rec(Category category, string description) => new(Guid.Empty, "", category, "r", description, FindingStatus.Open);

    [Fact] public async Task CheckFindsC2paAndChangesNothing()
    {
        var media = await site.UploadAsync("c2pa.jpg");
        var before = await site.ReadMediaFileAsync(media);
        var check = await Sut.CheckAsync(media, default);
        check.Findings.ShouldContain(f => f.Category == Category.Provenance && f.Status == FindingStatus.Open);
        check.Hash.ShouldNotBeNull().Length.ShouldBe(64);
        (await site.ReadMediaFileAsync(media)).ShouldBe(before);
    }

    [Fact] public async Task RemoveCameraKeepsProvenance()
    {
        var media = await site.UploadAsync(UmbracoSiteFixture.MixedJpeg(), "mixed.jpg");
        var check = await Sut.CheckAsync(media, default);
        var after = await Sut.RemoveAsync(media.Key, Category.Camera, check.Hash!, default);
        var bytes = await site.ReadMediaFileAsync(media);
        var inspection = ProvStripEngine.Inspect(bytes);
        inspection.Findings.ShouldNotContain(f => f.Category == Category.Camera);
        inspection.Findings.ShouldContain(f => f.Category == Category.Provenance);
        site.Reload(media).GetValue<long>(Constants.Conventions.Media.Bytes).ShouldBe(bytes.Length);
        after.Findings.ShouldContain(f => f.Category == Category.Camera && f.Status == FindingStatus.Removed);
        after.Findings.ShouldContain(f => f.Category == Category.Provenance && f.Status == FindingStatus.Open);
    }

    [Fact] public async Task RemoveKeepsTheUrl()
    {
        var media = await site.UploadAsync("camera.jpg");
        var url = site.UrlOf(media);
        var check = await Sut.CheckAsync(media, default);
        await Sut.RemoveAsync(media.Key, null, check.Hash!, default);
        site.UrlOf(site.Reload(media)).ShouldBe(url);
        var served = await site.AnonymousClient().GetByteArrayAsync(url);
        ProvStripEngine.Inspect(served).Findings.ShouldNotContain(f => f.Category == Category.Camera);
    }

    [Fact] public async Task RemoveRefusesAStaleHash()
    {
        var media = await site.UploadAsync("camera.jpg");
        var before = await site.ReadMediaFileAsync(media);
        await Should.ThrowAsync<StaleFileException>(() => Sut.RemoveAsync(media.Key, Category.Camera, new string('0', 64), default));
        (await site.ReadMediaFileAsync(media)).ShouldBe(before);
    }

    [Fact] public async Task MissingFileNeedsALook()
    {
        var media = await site.UploadAsync("camera.jpg");
        site.Resolve<MediaFileManager>().FileSystem.DeleteFile(site.RelativePathOf(media));
        var check = await Sut.CheckAsync(media, default);
        check.NeedsLook.ShouldBe("file not found");
    }

    [Fact] public async Task UnknownSegmentNeedsALook()
    {
        var media = await site.UploadAsync("unknown-ancillary.png");
        var check = await Sut.CheckAsync(media, default);
        check.NeedsLook.ShouldNotBeNull().ShouldContain("unknown");
        check.Findings.ShouldContain(f => f.Category == Category.Unknown && f.Status == FindingStatus.NeedsLook);
    }

    [Fact] public async Task NonImageIsIgnored()
    {
        var media = await site.UploadAsync(UmbracoSiteFixture.Pdf(), "doc.pdf", Constants.Conventions.MediaTypes.File);
        var check = await Sut.CheckAsync(media, default);
        (check.Findings.Count, check.NeedsLook).ShouldBe((0, (string?)null));
    }

    [Fact] public async Task OversizedFileNeedsALook()
    {
        var media = await site.UploadAsync("camera.png");
        var small = ActivatorUtilities.CreateInstance<MediaProvStrip>(site.Services, Options.Create(new ProvStripOptions { MaxFileBytes = 100 }));
        (await small.CheckAsync(media, default)).NeedsLook.ShouldBe("too large to check");
    }

    // ImageSharp keeps EXIF when it resizes, so a crop of the original keeps GPS. ImageSharp.Web 3.3 drops
    // unknown query keys (Umbraco's rnd cache buster among them) before it builds the cache key, and holds a
    // source's last-write time in memory for 30 seconds. So a crop already served can come from the old copy
    // for up to 30 seconds after the Remove; a crop not served before comes from the cleaned file at once.
    [Fact] public async Task CropsComeFromTheCleanedFileWithin30Seconds()
    {
        var media = await site.UploadAsync("camera.jpg");
        var client = site.AnonymousClient();
        var served = site.CropUrl(media, 40);
        ProvStripEngine.Inspect(await client.GetByteArrayAsync(served)).Findings.ShouldContain(f => f.Category == Category.Camera);
        var check = await Sut.CheckAsync(media, default);
        await Sut.RemoveAsync(media.Key, Category.Camera, check.Hash!, default);
        ProvStripEngine.Inspect(await client.GetByteArrayAsync(site.CropUrl(media, 41))).Findings.ShouldNotContain(f => f.Category == Category.Camera);
        await Task.Delay(TimeSpan.FromSeconds(31));
        ProvStripEngine.Inspect(await client.GetByteArrayAsync(served)).Findings.ShouldNotContain(f => f.Category == Category.Camera);
    }

    [Fact] public void LabelsNameTheCategoryAndWhat() =>
        Labels.For([Rec(Category.Provenance, "C2PA"), Rec(Category.Camera, "GPS"), Rec(Category.Camera, "camera model")])
            .ShouldBe(["AI attribution found (C2PA)", "Camera data found (GPS, camera model)"]);
}

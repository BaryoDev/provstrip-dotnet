using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Tests;

internal static class FindingExtensions
{
    public static string Label(this Finding f) => f.Segment.Label;
}

public class JpegContainerTests
{
    static readonly JpegContainer Jpeg = new();

    static byte[] StripAll(string name)
    {
        var input = Fixtures.Read(name);
        return ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings).Output;
    }

    static byte[] ScanBytes(byte[] jpeg) =>
        Jpeg.ReadSegments(jpeg).Where(s => s.Label == "SOS").SelectMany(s => jpeg[s.Start..s.End]).ToArray();

    static int Count(string name, string label) => Jpeg.ReadSegments(Fixtures.Read(name)).Count(s => s.Label == label);

    [Fact] public void C2paJpegHasApp11Provenance() =>
        ProvStripEngine.Inspect(Fixtures.Read("c2pa.jpg")).Findings.ShouldContain(f => f.RuleId == "jpeg-c2pa");

    [Theory, InlineData("c2pa.jpg"), InlineData("camera.jpg"), InlineData("mixed-xmp.jpg"), InlineData("iptc.jpg"), InlineData("icc-machine.jpg")]
    public void StrippingEverythingLeavesClean(string name) => ProvStripEngine.Inspect(StripAll(name)).IsClean.ShouldBeTrue();

    [Fact] public void MixedXmpIsOneProvenanceFinding() =>
        ProvStripEngine.Inspect(Fixtures.Read("mixed-xmp.jpg")).Findings.Where(f => f.Label() == "APP1" && f.RuleId == "jpeg-xmp")
            .ShouldHaveSingleItem().Category.ShouldBe(Category.Provenance);

    [Fact] public void ScanDataIsByteIdentical() => ScanBytes(StripAll("camera.jpg")).ShouldBe(ScanBytes(Fixtures.Read("camera.jpg")));

    [Fact] public void ProgressiveKeepsEverySos() => Count("progressive.jpg", "SOS").ShouldBeGreaterThan(1);

    [Fact] public void MpfTrailerIsKept() => ProvStripEngine.Inspect(Fixtures.Read("mpf-trailer.jpg")).IsClean.ShouldBeTrue();

    [Fact] public void TrailerWithoutMpfIsUnknownAndDeletable() =>
        ProvStripEngine.Inspect(Fixtures.Read("trailer-no-mpf.jpg")).Findings.ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(f => f.Category.ShouldBe(Category.Unknown), f => f.CanDelete.ShouldBeTrue());

    [Fact] public void MissingEoiThrows() => Should.Throw<ProvStripFormatException>(() => ProvStripEngine.Inspect(Fixtures.Read("no-eoi.jpg")));

    [Fact] public void StandardIccIsKept() => ProvStripEngine.Inspect(Fixtures.Read("icc-standard.jpg")).IsClean.ShouldBeTrue();

    [Theory, InlineData("c2pa.jpg"), InlineData("progressive.jpg"), InlineData("mpf-trailer.jpg")]
    public void SegmentsCoverTheWholeFile(string name)
    {
        var bytes = Fixtures.Read(name);
        var segs = Jpeg.ReadSegments(bytes);
        segs[0].ShouldSatisfyAllConditions(s => s.Start.ShouldBe(0), s => s.Label.ShouldBe("SOI"));
        segs.Zip(segs.Skip(1)).ShouldAllBe(p => p.First.End == p.Second.Start);
        segs[^1].End.ShouldBe(bytes.Length);
    }

    [Fact] public void App11KeywordIsJp() =>
        Jpeg.ReadSegments(Fixtures.Read("c2pa.jpg")).Where(s => s.Label == "APP11").ShouldAllBe(s => s.Keyword == "JP");

    [Fact] public void CameraStripKeepsOrientationInApp1()
    {
        var output = StripAll("camera.jpg");
        Exif.Read(Bytes.JpegPayload(output, 0xE1, "Exif")).Orientation.ShouldBe((ushort)6);
    }

    [Fact] public void LengthPastTheEndThrows()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE1, 0x10, 0x00, 0x45, 0x78];
        Should.Throw<ProvStripFormatException>(() => Jpeg.ReadSegments(bytes));
    }

    [Fact] public void MissingSoiThrows() =>
        Should.Throw<ProvStripFormatException>(() => Jpeg.ReadSegments(new byte[] { 0xFF, 0xD9 }));
}

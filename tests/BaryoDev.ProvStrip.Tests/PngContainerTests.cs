using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Tests;

public class PngContainerTests
{
    static readonly PngContainer Png = new();

    static IReadOnlyList<Segment> Segments(byte[] file) => Png.ReadSegments(file);
    static IReadOnlyList<Segment> Segments(string name) => Segments(Fixtures.Read(name));

    static byte[] ConcatKept(byte[] input, IEnumerable<Segment> segs, string dropLabel) =>
        segs.Where(s => s.Label != dropLabel).SelectMany(s => input[s.Start..s.End]).ToArray();

    [Fact] public void CleanPngIsAllStructural() =>
        Segments("clean.png").ShouldAllBe(s => Png.IsStructural(s, Fixtures.Read("clean.png")));

    [Fact] public void SegmentsCoverTheWholeFileAfterTheSignature()
    {
        var segs = Segments("c2pa.png");
        segs[0].Start.ShouldBe(8);
        segs.Zip(segs.Skip(1)).ShouldAllBe(p => p.First.End == p.Second.Start);
        segs[^1].End.ShouldBe(Fixtures.Read("c2pa.png").Length);
    }

    [Fact] public void C2paPngHasCaBX() => Segments("c2pa.png").Select(s => s.Label).ShouldContain("caBX");

    [Fact] public void TextChunksCarryTheirKeyword() =>
        Segments("tool-xmp.png").Where(s => s.Label is "iTXt" or "tEXt").Select(s => s.Keyword).ShouldBe(["XML:com.adobe.xmp", "Software"]);

    [Fact] public void PayloadIsTheChunkData()
    {
        var input = Fixtures.Read("tool-xmp.png");
        var software = Segments(input).Single(s => s.Label == "tEXt");
        Png.Payload(software, input).ToArray().ShouldBe("Software\0Fixture Tool 1.0"u8.ToArray());
    }

    [Fact] public void DroppingCaBXKeepsEveryOtherChunkByteForByte()
    {
        var input = Fixtures.Read("c2pa.png");
        var segs = Segments("c2pa.png");
        var output = Png.Rewrite(input, segs.Where(s => s.Label == "caBX").Select(s => new SegmentEdit(s, null)).ToList());
        output[..8].ShouldBe(input[..8]);
        output[8..].ShouldBe(ConcatKept(input, segs, "caBX"));
    }

    [Fact] public void ReplacementGetsAValidCrcAndKeepsItsPlace()
    {
        var input = Fixtures.Read("camera.png");
        var exif = Segments(input).Single(s => s.Label == "eXIf");
        var replacement = Exif.OrientationOnly(Png.Payload(exif, input).Span)!;
        var output = Png.Rewrite(input, [new SegmentEdit(exif, replacement)]);
        var after = Segments(output);
        after.Select(s => s.Label).ShouldBe(Segments(input).Select(s => s.Label));
        Png.Payload(after.Single(s => s.Label == "eXIf"), output).ToArray().ShouldBe(replacement);
    }

    [Fact] public void BadCrcNamesTheOffset() =>
        Should.Throw<ProvStripFormatException>(() => Segments("bad-crc.png")).Offset.ShouldBe(8);

    [Fact] public void TruncatedThrows() => Should.Throw<ProvStripFormatException>(() => Segments("truncated.png"));

    [Fact] public void NotAPngThrows() => Should.Throw<ProvStripFormatException>(() => Segments("clean.jpg"));

    [Fact] public void AncillaryUnknownIsSafeToDrop() => Png.IsSafeToDrop(new Segment(0, 12, "prVt")).ShouldBeTrue();

    [Fact] public void CriticalUnknownIsNot() => Png.IsSafeToDrop(new Segment(0, 12, "QXQX")).ShouldBeFalse();

    [Fact] public void IccpIsNotStructural() => Png.IsStructural(new Segment(0, 12, "iCCP"), []).ShouldBeFalse();

    [Fact] public void BytesAfterIendAreATrailerSegment()
    {
        byte[] input = [.. Fixtures.Read("clean.png"), .. "hidden after IEND"u8];
        var last = Segments(input)[^1];
        last.Label.ShouldBe("TRAILER");
        last.End.ShouldBe(input.Length);
        Png.IsStructural(last, input).ShouldBeFalse();
        Png.IsSafeToDrop(last).ShouldBeTrue();
        Png.Payload(last, input).ToArray().ShouldBe("hidden after IEND"u8.ToArray());
    }

    [Fact] public void DroppingTheTrailerLeavesTheImage()
    {
        var clean = Fixtures.Read("clean.png");
        byte[] input = [.. clean, .. "hidden"u8];
        Png.Rewrite(input, [new SegmentEdit(Segments(input)[^1], null)]).ShouldBe(clean);
    }

    [Fact] public void SuggestedPaletteNameReachesTheRules()
    {
        var clean = Fixtures.Read("clean.png");
        byte[] input = [.. clean[..^12], .. Bytes.PngChunk("sPLT", [.. "Fixture Tool"u8, 0, 8, 0, 0, 0, 0, 0, 0]), .. clean[^12..]];
        var splt = Segments(input).Single(s => s.Label == "sPLT");
        Png.IsStructural(splt, input).ShouldBeFalse();
        splt.Keyword.ShouldBe("Fixture Tool");
    }

    // A chunk with a fixed layout is image structure only at that length. Extra bytes could carry anything.
    [Theory]
    [InlineData("gAMA", 4, true)] [InlineData("gAMA", 5, false)]
    [InlineData("cHRM", 32, true)] [InlineData("cHRM", 40, false)]
    [InlineData("sRGB", 1, true)] [InlineData("sRGB", 9, false)]
    [InlineData("pHYs", 9, true)] [InlineData("pHYs", 10, false)]
    [InlineData("cICP", 4, true)] [InlineData("cICP", 8, false)]
    [InlineData("mDCv", 24, true)] [InlineData("mDCv", 30, false)]
    [InlineData("cLLi", 8, true)] [InlineData("cLLi", 9, false)]
    [InlineData("acTL", 8, true)] [InlineData("acTL", 12, false)]
    [InlineData("fcTL", 26, true)] [InlineData("fcTL", 27, false)]
    [InlineData("sBIT", 4, true)] [InlineData("sBIT", 5, false)]
    [InlineData("bKGD", 6, true)] [InlineData("bKGD", 3, false)]
    public void FixedLengthChunkIsStructuralOnlyAtItsLength(string type, int length, bool structural)
    {
        var input = Bytes.CleanPngWith(type, new byte[length]);
        var chunk = Segments(input).Single(s => s.Label == type);
        Png.IsStructural(chunk, input).ShouldBe(structural);
    }

    [Fact]
    public void LongIhdrAndIendAreNotStructural()
    {
        var clean = Fixtures.Read("clean.png");
        byte[] longIhdr = [.. clean[..8], .. Bytes.PngChunk("IHDR", [.. clean[16..29], 0]), .. clean[33..]];
        Png.IsStructural(Segments(longIhdr).Single(s => s.Label == "IHDR"), longIhdr).ShouldBeFalse();
        byte[] longIend = [.. clean[..^12], .. Bytes.PngChunk("IEND", [1, 2, 3])];
        Png.IsStructural(Segments(longIend).Single(s => s.Label == "IEND"), longIend).ShouldBeFalse();
    }
}

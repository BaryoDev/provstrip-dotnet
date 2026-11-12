using System.Buffers.Binary;
using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.ProvStrip.Tests;

public class WebpContainerTests
{
    static readonly WebpContainer Webp = new();

    static byte[] StripWhere(byte[] input, Func<Finding, bool> pick) =>
        ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings.Where(pick).ToList()).Output;

    static byte[] StripCategory(string name, Category category) => StripWhere(Fixtures.Read(name), f => f.Category == category);

    static byte[] StripAll(string name) => StripWhere(Fixtures.Read(name), _ => true);

    static Segment Vp8x(byte[] webp) => Webp.ReadSegments(webp).Single(s => s.Label == "VP8X");

    static int Vp8xFlags(byte[] webp) => webp[Vp8x(webp).Start + 8];

    static int Vp8xFlagOffset(string name) => Vp8x(Fixtures.Read(name)).Start + 8;

    static bool HasChunk(byte[] webp, string label) => Webp.ReadSegments(webp).Any(s => s.Label == label);

    static int Count(byte[] webp, string label) => Webp.ReadSegments(webp).Count(s => s.Label == label);

    // Input offsets that differ, in the RIFF header and in every chunk no finding touched.
    static List<int> ChangedOffsetsOutsideDroppedChunks(string name)
    {
        var input = Fixtures.Read(name);
        var touched = ProvStripEngine.Inspect(input).Findings.Where(f => f.Category == Category.Camera).Select(f => f.Segment.Label).ToHashSet();
        var output = StripCategory(name, Category.Camera);
        var changed = Enumerable.Range(0, 12).Where(o => input[o] != output[o]).ToList();
        var before = Webp.ReadSegments(input).Where(s => !touched.Contains(s.Label)).ToList();
        var after = Webp.ReadSegments(output).Where(s => !touched.Contains(s.Label)).ToList();
        after.Select(s => s.Label).ShouldBe(before.Select(s => s.Label));
        foreach (var (a, b) in before.Zip(after))
        {
            b.Length.ShouldBe(a.Length);
            changed.AddRange(Enumerable.Range(0, a.Length).Where(k => input[a.Start + k] != output[b.Start + k]).Select(k => a.Start + k));
        }
        return changed;
    }

    [Fact] public void C2paWebpIsProvenance() =>
        ProvStripEngine.Inspect(Fixtures.Read("c2pa.webp")).Findings.ShouldContain(f => f.RuleId == "webp-c2pa");

    [Fact] public void OrientationKeepsTheExifChunkAndFlag() =>
        (Vp8xFlags(StripCategory("camera.webp", Category.Camera)) & 0x08).ShouldBe(0x08);

    [Fact] public void ExifWithoutOrientationClearsTheFlag()
    {
        var output = StripCategory("exif-no-orientation.webp", Category.Camera);
        HasChunk(output, "EXIF").ShouldBeFalse();
        (Vp8xFlags(output) & 0x08).ShouldBe(0);
    }

    [Fact] public void XmpFlagClearedWhenXmpGoes()
    {
        var output = StripAll("c2pa.webp");
        if (!HasChunk(output, "XMP ") && HasChunk(output, "VP8X")) (Vp8xFlags(output) & 0x04).ShouldBe(0);
    }

    [Fact] public void RiffSizeMatchesLength()
    {
        var output = StripAll("c2pa.webp");
        BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4)).ShouldBe((uint)(output.Length - 8));
    }

    [Fact] public void OnlyTheFlagByteAndRiffSizeChangeInKeptHeaders() =>
        ChangedOffsetsOutsideDroppedChunks("camera.webp").ShouldBeSubsetOf([4, 5, 6, 7, Vp8xFlagOffset("camera.webp")]);

    [Fact] public void AnimatedKeepsEveryFrame() =>
        Count(StripAll("animated.webp"), "ANMF").ShouldBe(Count(Fixtures.Read("animated.webp"), "ANMF"));

    [Fact] public void RiffLongerThanFileThrows() =>
        Should.Throw<ProvStripFormatException>(() => ProvStripEngine.Inspect(Fixtures.Read("riff-too-long.webp")));

    [Fact] public void StrippedC2paWebpIsClean() => ProvStripEngine.Inspect(StripAll("c2pa.webp")).IsClean.ShouldBeTrue();

    [Fact] public void XmpLabelKeepsItsSpace() =>
        Webp.ReadSegments(Fixtures.Read("c2pa.webp")).Concat(Webp.ReadSegments(Fixtures.Read("camera.webp")))
            .Where(s => s.Label.StartsWith("XMP")).ShouldAllBe(s => s.Label == "XMP ");

    [Fact] public void BytesAfterTheRiffAreATrailer()
    {
        byte[] input = [.. Fixtures.Read("clean.webp"), .. "hidden"u8];
        var finding = ProvStripEngine.Inspect(input).Findings.ShouldHaveSingleItem();
        finding.Description.ShouldBe("unknown TRAILER segment");
        ProvStripEngine.Strip(input, [finding]).Output.ShouldBe(Fixtures.Read("clean.webp"));
    }
}

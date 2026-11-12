using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.ProvStrip.Tests;

public class GuaranteeTests
{
    static readonly string[] Broken = ["truncated.png", "bad-crc.png", "no-eoi.jpg", "riff-too-long.webp", "unknown-critical.png"];

    public static TheoryData<string> AllStrippableFixtures() => new(Fixtures.All().Except(Broken).Order());

    static IContainer ContainerFor(string format) => format switch
    {
        "png" => new PngContainer(),
        "jpeg" => new JpegContainer(),
        "webp" => new WebpContainer(),
        _ => new SvgContainer(),
    };

    static StripResult StripDeletable(byte[] input) =>
        ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings.Where(f => f.CanDelete).ToList());

    static byte[] StripAll(string name) => StripDeletable(Fixtures.Read(name)).Output;

    // Walks the output's segments against the input's kept ones, in order. A rewritten EXIF block may appear
    // in place of a removed one, and VP8X may differ in its flag byte; nothing else may change.
    static List<string> KeptSegmentsDiffer(string name)
    {
        var input = Fixtures.Read(name);
        var result = StripDeletable(input);
        var container = ContainerFor(ProvStripEngine.Inspect(input).Format);
        var removed = result.Removed.Select(f => f.Segment.Start).ToHashSet();
        var kept = container.ReadSegments(input).Where(s => !removed.Contains(s.Start)).ToList();
        var output = result.Output;

        var differences = new List<string>();
        var k = 0;
        foreach (var o in container.ReadSegments(output))
        {
            if (k < kept.Count && kept[k].Label == o.Label)
            {
                var a = input.AsSpan(kept[k].Start, kept[k].Length);
                var b = output.AsSpan(o.Start, o.Length);
                var same = o.Label == "VP8X"
                    ? a.Length == b.Length && a[..8].SequenceEqual(b[..8]) && a[9..].SequenceEqual(b[9..])
                    : a.SequenceEqual(b);
                if (!same) differences.Add($"{o.Label} at {kept[k].Start} changed");
                k++;
            }
            else if (o.Label is not ("eXIf" or "APP1" or "EXIF"))
                differences.Add($"unexpected {o.Label} at {o.Start}");
        }
        if (k != kept.Count) differences.Add($"{kept.Count - k} kept segments missing");
        return differences;
    }

    [Theory, MemberData(nameof(AllStrippableFixtures))]
    public void EveryKeptSegmentIsByteIdentical(string name) => KeptSegmentsDiffer(name).ShouldBeEmpty();

    [Theory, MemberData(nameof(AllStrippableFixtures))]
    public void StrippingTwiceChangesNothing(string name)
    {
        var once = StripAll(name);
        ProvStripEngine.Strip(once, []).Output.ShouldBe(once);
        ProvStripEngine.Inspect(once).IsClean.ShouldBeTrue();
    }

    [Fact] public void FormatComesFromBytesNotExtension()
    {
        var path = Temp.Copy("c2pa.png", @as: "photo.jpg");
        FileStripper.Run(path, new StripPolicy(CategoryMode.Auto)).Format.ShouldBe("png");
    }
}

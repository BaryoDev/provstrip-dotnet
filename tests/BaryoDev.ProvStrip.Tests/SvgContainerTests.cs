using System.Text;
using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.ProvStrip.Tests;

public class SvgContainerTests
{
    static readonly SvgContainer Svg = new();

    static byte[] StripAll(string name)
    {
        var input = Fixtures.Read(name);
        return ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings).Output;
    }

    // Removes each range from the input, skipping a range nested inside one already cut.
    static byte[] CutRanges(byte[] input, IEnumerable<Segment> ranges)
    {
        var output = new List<byte>();
        var at = 0;
        foreach (var r in ranges.OrderBy(r => r.Start).ThenByDescending(r => r.Length))
        {
            if (r.Start < at) continue;
            output.AddRange(input[at..r.Start]);
            at = r.End;
        }
        output.AddRange(input[at..]);
        return output.ToArray();
    }

    static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact] public void C2paSvgIsProvenance() =>
        ProvStripEngine.Inspect(Fixtures.Read("c2pa.svg")).Findings.ShouldContain(f => f.Category == Category.Provenance);

    [Fact] public void GeneratorCommentAndEditorDataAreFound() =>
        ProvStripEngine.Inspect(Fixtures.Read("generator-comment.svg")).Findings.Select(f => f.RuleId).Distinct()
            .ShouldBe(["svg-generator", "svg-editor"], ignoreOrder: true);

    [Fact] public void OutputIsInputWithOnlyTheFoundRangesCut()
    {
        var input = Fixtures.Read("c2pa.svg");
        var found = ProvStripEngine.Inspect(input).Findings.Select(f => f.Segment);
        StripAll("c2pa.svg").ShouldBe(CutRanges(input, found));
    }

    [Fact] public void TitleAndDescAreKept() =>
        ProvStripEngine.Inspect("<svg><title>t</title><desc>d</desc></svg>"u8.ToArray()).IsClean.ShouldBeTrue();

    [Fact] public void MarkerInsideCdataIsNotATag() =>
        ProvStripEngine.Inspect("<svg><style><![CDATA[ .a{} /* <metadata> */ ]]></style></svg>"u8.ToArray()).IsClean.ShouldBeTrue();

    [Fact] public void UnclosedMetadataThrows() =>
        Should.Throw<ProvStripFormatException>(() => ProvStripEngine.Inspect("<svg><metadata>x</svg>"u8.ToArray()));

    [Fact] public void NamespaceDeclarationIsLabelledByItsPrefix() =>
        Svg.ReadSegments(Fixtures.Read("c2pa.svg")).Select(s => s.Label).ShouldContain("c2pa:xmlns");

    [Fact] public void StrippedEditorSvgKeepsTheDrawingAndPlainComment()
    {
        var output = Text(StripAll("generator-comment.svg"));
        output.ShouldContain("""<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16">""");
        output.ShouldContain("<!-- a plain comment that stays -->");
        output.ShouldContain("""<rect width="16" height="16" fill="#3366cc"/>""");
        output.ShouldNotContain("inkscape");
        output.ShouldNotContain("sodipodi");
        output.ShouldNotContain("Generator");
    }

    [Fact] public void PlainCommentIsNotReported() =>
        ProvStripEngine.Inspect("<svg><!-- hello --><rect/></svg>"u8.ToArray()).IsClean.ShouldBeTrue();

    [Fact] public void MarkerInsideAnAttributeValueIsNotATag() =>
        ProvStripEngine.Inspect("""<svg><text data-x="<metadata>">a</text></svg>"""u8.ToArray()).IsClean.ShouldBeTrue();

    [Fact] public void RemovingTheOuterElementReportsWhatWasInsideIt()
    {
        var input = Fixtures.Read("c2pa.svg");
        var metadata = ProvStripEngine.Inspect(input).Findings.Single(f => f.Segment.Label == "metadata");
        var result = ProvStripEngine.Strip(input, [metadata]);
        result.Removed.Select(f => f.Segment.Label).ShouldBe(["metadata", "c2pa:manifest"], ignoreOrder: true);
        Text(result.Output).ShouldNotContain("<metadata>");
    }

    [Fact] public void SelfClosingMetadataIsOneSegment() =>
        Svg.ReadSegments("<svg><metadata/></svg>"u8.ToArray()).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.Label.ShouldBe("metadata"), s => s.Start.ShouldBe(5), s => s.Length.ShouldBe(11));
}

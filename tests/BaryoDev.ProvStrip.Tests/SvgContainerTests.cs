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

    static IReadOnlyList<Finding> Find(string svg) => ProvStripEngine.Inspect(Encoding.UTF8.GetBytes(svg)).Findings;

    const string SvgNs = "xmlns=\"http://www.w3.org/2000/svg\"";

    [Fact] public void AnyPrefixBoundToTheC2paNamespaceIsFound() =>
        Find($"""<svg {SvgNs} xmlns:x="http://c2pa.org/manifest"><x:manifest>abc</x:manifest></svg>""")
            .ShouldContain(f => f.Segment.Label == "c2pa:manifest" && f.Category == Category.Provenance);

    [Fact] public void DefaultNamespaceC2paIsFound() =>
        Find($"""<svg {SvgNs}><manifest xmlns="http://c2pa.org/manifest">abc</manifest></svg>""")
            .ShouldContain(f => f.Segment.Label == "c2pa:manifest" && f.Category == Category.Provenance);

    [Fact] public void ThePrefixC2paBoundElsewhereIsNotTrustedByName() =>
        Find($"""<svg {SvgNs} xmlns:c2pa="http://example.com/other"><c2pa:x/></svg>""")
            .ShouldAllBe(f => f.Category == Category.Unknown);

    [Fact] public void AnotherNamespaceIsAnUnknownThatCanGo()
    {
        var input = Encoding.UTF8.GetBytes($"""<svg {SvgNs} xmlns:sketch="http://www.bohemiancoding.com/sketch/ns"><g sketch:type="MSPage"/></svg>""");
        var findings = ProvStripEngine.Inspect(input).Findings;
        findings.Select(f => f.Segment.Label).ShouldBe(["xmlns:sketch", "sketch:type"], ignoreOrder: true);
        findings.ShouldAllBe(f => f.Category == Category.Unknown && f.CanDelete);
        Text(ProvStripEngine.Strip(input, findings).Output).ShouldBe($"""<svg {SvgNs}><g/></svg>""");
    }

    [Fact] public void XlinkAndXmlAttributesAreKept() =>
        Find($"""<svg {SvgNs} xmlns:xlink="http://www.w3.org/1999/xlink" xml:space="preserve"><use xlink:href="#a"/></svg>""").ShouldBeEmpty();

    [Fact] public void NamespacesInsideMetadataAreNotSeparateFindings() =>
        Find($"""<svg {SvgNs}><metadata><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"/></metadata></svg>""")
            .ShouldHaveSingleItem().Segment.Label.ShouldBe("metadata");

    [Fact] public void InternalDtdSubsetIsAnUnknownThatStays() =>
        Find($"""<!DOCTYPE svg [<!ENTITY a "x">]><svg {SvgNs}>&a;</svg>""").ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Segment.Label.ShouldBe("#doctype"), f => f.Category.ShouldBe(Category.Unknown), f => f.CanDelete.ShouldBeFalse());

    [Fact] public void ApostropheInASubsetCommentDoesNotHideTags() =>
        Find($"""<!DOCTYPE svg [<!-- it's -->]><svg {SvgNs}><metadata/></svg>""").Select(f => f.Segment.Label)
            .ShouldBe(["#doctype", "metadata"], ignoreOrder: true);

    [Fact] public void StylesheetInstructionIsAnUnknown() =>
        Find($"""<?xml version="1.0"?><?xml-stylesheet href="a.css"?><svg {SvgNs}/>""").ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(f => f.Segment.Label.ShouldBe("#pi"), f => f.CanDelete.ShouldBeTrue());

    [Fact] public void MismatchedEndTagThrows() =>
        Should.Throw<ProvStripFormatException>(() => Find($"""<svg {SvgNs}><g></svg>"""));

    [Fact] public void StandardSvgDoctypeIsKept() =>
        Find($"""<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd"><svg {SvgNs}/>""").ShouldBeEmpty();

    [Fact] public void OtherExternalDoctypeIsAnUnknown() =>
        Find($"""<!DOCTYPE svg SYSTEM "http://example.com/x.dtd"><svg {SvgNs}/>""").ShouldHaveSingleItem().Segment.Label.ShouldBe("#doctype");

    [Fact] public void XmlDeclarationWithExtraContentIsAnInstruction() =>
        Find($"""<?xml version="1.0" generator="Tool"?><svg {SvgNs}/>""").ShouldHaveSingleItem().Segment.Label.ShouldBe("#pi");

    [Fact] public void XmlDeclarationAfterTheStartIsAnInstruction() =>
        Find($"""<?xml version="1.0"?><svg {SvgNs}><?xml version="1.0"?></svg>""").ShouldHaveSingleItem().Segment.Label.ShouldBe("#pi");
}

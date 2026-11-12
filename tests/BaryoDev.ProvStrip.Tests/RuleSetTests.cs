using System.Text;
using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Tests;

public class RuleSetTests
{
    static RuleOutcome.Match Classify(string format, string label, string keyword, string payload) =>
        RuleSet.Default.Classify(format, new Segment(0, payload.Length, label, keyword), Encoding.UTF8.GetBytes(keyword + "\0" + payload))
            .ShouldBeOfType<RuleOutcome.Match>();

    static string OneRule(string rule) => $$"""{"provenanceMarkers":[],"standardIccDescriptions":[],"rules":[{{rule}}]}""";

    [Fact] public void CaBXIsProvenance() =>
        RuleSet.Default.Classify("png", new Segment(8, 20, "caBX"), []).ShouldBe(new RuleOutcome.Match(Category.Provenance, "png-cabx", "C2PA"));

    [Fact] public void PngSoftwareTextIsProvenance() =>
        RuleSet.Default.Classify("png", new Segment(8, 20, "tEXt", "Software"), "Software\0x"u8).ShouldBeOfType<RuleOutcome.Match>().Category.ShouldBe(Category.Provenance);

    [Fact] public void PngC2paKeywordGlob() =>
        RuleSet.Default.Classify("png", new Segment(8, 20, "iTXt", "c2pa.manifest"), "c2pa.manifest\0x"u8).ShouldBeOfType<RuleOutcome.Match>().RuleId.ShouldBe("png-text-c2pa");

    [Fact] public void OtherPngTextIsCamera() =>
        RuleSet.Default.Classify("png", new Segment(8, 20, "tEXt", "Author"), "Author\0me"u8).ShouldBe(new RuleOutcome.Match(Category.Camera, "png-text", "text"));

    [Fact] public void XmpWithCreatorToolIsProvenance() =>
        Classify("jpeg", "APP1", "http://ns.adobe.com/xap/1.0/", "<x:xmpmeta><rdf:Description xmp:CreatorTool=\"T\"/></x:xmpmeta>")
            .ShouldBe(new RuleOutcome.Match(Category.Provenance, "jpeg-xmp", "XMP naming a tool"));

    [Fact] public void XmpWithOnlyATitleIsCamera() =>
        Classify("jpeg", "APP1", "http://ns.adobe.com/xap/1.0/", "<x:xmpmeta><dc:title>t</dc:title></x:xmpmeta>")
            .ShouldBe(new RuleOutcome.Match(Category.Camera, "jpeg-xmp", "XMP"));

    [Fact] public void MarkerMatchIgnoresCase() =>
        Classify("webp", "XMP ", "", "<x:xmpmeta CREATORTOOL=\"T\"/>").Category.ShouldBe(Category.Provenance);

    [Fact] public void SvgPrefixGlob() =>
        RuleSet.Default.Classify("svg", new Segment(0, 10, "c2pa:manifest"), []).ShouldBeOfType<RuleOutcome.Match>().RuleId.ShouldBe("svg-c2pa");

    [Fact] public void SvgGeneratorCommentNeedsTheWord() =>
        RuleSet.Default.Classify("svg", new Segment(0, 10, "#comment"), "<!-- Generator: X -->"u8).ShouldBeOfType<RuleOutcome.Match>().RuleId.ShouldBe("svg-generator");

    [Fact] public void SvgPlainCommentHasNoRule() =>
        RuleSet.Default.Classify("svg", new Segment(0, 10, "#comment"), "<!-- hello -->"u8).ShouldBe(RuleOutcome.NoRule);

    [Fact] public void UnlistedChunkHasNoRule() =>
        RuleSet.Default.Classify("png", new Segment(8, 20, "prVt"), []).ShouldBe(RuleOutcome.NoRule);

    [Fact] public void OtherFormatsRulesDoNotApply() =>
        RuleSet.Default.Classify("jpeg", new Segment(8, 20, "caBX"), []).ShouldBe(RuleOutcome.NoRule);

    [Fact] public void DuplicateRuleIdIsRejected() =>
        Should.Throw<ProvStripException>(() => RuleSet.Parse(
            """{"provenanceMarkers":[],"standardIccDescriptions":[],"rules":[{"id":"a","format":"png","label":"x","category":"camera","describe":"d"},{"id":"a","format":"png","label":"y","category":"camera","describe":"d"}]}"""))
            .Message.ShouldContain("\"a\"");

    [Fact] public void UnknownCategoryIsRejected() =>
        Should.Throw<ProvStripException>(() => RuleSet.Parse(OneRule("""{"id":"x","format":"png","label":"x","category":"bogus"}"""))).Message.ShouldContain("\"x\"");

    [Fact] public void UnknownClassifyIsRejected() =>
        Should.Throw<ProvStripException>(() => RuleSet.Parse(OneRule("""{"id":"x","format":"png","label":"x","classify":"bogus"}""")));

    [Fact] public void LoadReadsAFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, OneRule("""{"id":"mine","format":"png","label":"prVt","category":"camera","describe":"private"}"""));
        RuleSet.Load(path).Classify("png", new Segment(0, 1, "prVt"), []).ShouldBe(new RuleOutcome.Match(Category.Camera, "mine", "private"));
    }
}

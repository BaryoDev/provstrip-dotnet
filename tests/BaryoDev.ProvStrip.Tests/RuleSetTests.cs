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

    // Image generators write their settings as PNG text: Automatic1111 under "parameters", ComfyUI under "prompt" and "workflow".
    [Theory, InlineData("parameters"), InlineData("prompt"), InlineData("workflow")]
    public void GeneratorSettingsInPngTextAreProvenance(string keyword) =>
        Classify("png", "tEXt", keyword, "{\"steps\": 20}").Category.ShouldBe(Category.Provenance);

    // IPTC digital source types that say a model made or helped make the image.
    [Theory]
    [InlineData("trainedAlgorithmicMedia")]
    [InlineData("compositeWithTrainedAlgorithmicMedia")]
    [InlineData("algorithmicMedia")]
    [InlineData("compositeSynthetic")]
    public void AiDigitalSourceTypeInXmpIsProvenance(string code)
    {
        var xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:Iptc4xmpExt=\"http://iptc.org/std/Iptc4xmpExt/2008-02-29/\" Iptc4xmpExt:DigitalSourceType=\"http://cv.iptc.org/newscodes/digitalsourcetype/{code}\"/></rdf:RDF></x:xmpmeta>";
        var payload = Encoding.UTF8.GetBytes(xmp);
        RuleSet.Default.Classify("webp", new Segment(0, payload.Length, "XMP "), payload)
            .ShouldBeOfType<RuleOutcome.Match>().Category.ShouldBe(Category.Provenance);
    }

    [Fact] public void DigitalCaptureSourceTypeIsNotProvenance()
    {
        var payload = Encoding.UTF8.GetBytes("<x:xmpmeta><rdf:Description Iptc4xmpExt:DigitalSourceType=\"http://cv.iptc.org/newscodes/digitalsourcetype/digitalCapture\"/></x:xmpmeta>");
        RuleSet.Default.Classify("webp", new Segment(0, payload.Length, "XMP "), payload)
            .ShouldBeOfType<RuleOutcome.Match>().Category.ShouldBe(Category.Camera);
    }

    [Fact] public void InkscapeCreatedWithCommentIsProvenance()
    {
        var payload = Encoding.UTF8.GetBytes(" Created with Inkscape (http://www.inkscape.org/) ");
        RuleSet.Default.Classify("svg", new Segment(0, payload.Length, "#comment"), payload)
            .ShouldBe(new RuleOutcome.Match(Category.Provenance, "svg-generator", "generator comment"));
    }

    // A little-endian TIFF block with one IFD of ASCII entries, values inline when they fit in four bytes.
    static byte[] ExifAscii(params (ushort Tag, string Value)[] entries)
    {
        var values = entries.Select(e => Encoding.ASCII.GetBytes(e.Value + "\0")).ToArray();
        var ifdEnd = 8 + 2 + entries.Length * 12 + 4;
        var tiff = new List<byte>([.. "II*\0"u8, 8, 0, 0, 0, (byte)entries.Length, 0]);
        var extra = new List<byte>();
        for (var i = 0; i < entries.Length; i++)
        {
            tiff.AddRange(BitConverter.GetBytes(entries[i].Tag));
            tiff.AddRange([2, 0]);
            tiff.AddRange(BitConverter.GetBytes((uint)values[i].Length));
            if (values[i].Length <= 4) tiff.AddRange([.. values[i], .. new byte[4 - values[i].Length]]);
            else { tiff.AddRange(BitConverter.GetBytes((uint)(ifdEnd + extra.Count))); extra.AddRange(values[i]); }
        }
        tiff.AddRange([0, 0, 0, 0]);
        return [.. tiff, .. extra];
    }

    static RuleOutcome ClassifyExif(byte[] tiff) => RuleSet.Default.Classify("webp", new Segment(0, tiff.Length, "EXIF"), tiff);

    // Phones fill Software with an OS version. That is camera data, not AI attribution.
    [Fact] public void PhoneOsVersionInSoftwareIsCameraData() =>
        ClassifyExif(ExifAscii((0x0110, "iPhone 15"), (0x0131, "17.4.1"))).ShouldBe(new RuleOutcome.Match(Category.Camera, "webp-exif", "camera model"));

    [Fact] public void ShortSoftwareValueAloneIsCameraData() =>
        ClassifyExif(ExifAscii((0x0131, "1.0"))).ShouldBe(new RuleOutcome.Match(Category.Camera, "webp-exif", "EXIF"));

    // Software that names an AI tool or an editor is provenance.
    [Theory, InlineData("Adobe Photoshop 25.0 (Macintosh)"), InlineData("Midjourney"), InlineData("ComfyUI")]
    public void ToolInSoftwareIsProvenance(string software) =>
        ClassifyExif(ExifAscii((0x0131, software))).ShouldBe(new RuleOutcome.Match(Category.Provenance, "webp-exif", "Software tag"));

    // The block goes as provenance, but the report still names the camera data riding in it.
    [Fact] public void ToolSoftwareWithCameraDataNamesBoth() =>
        ClassifyExif(ExifAscii((0x0110, "Cam"), (0x0131, "Adobe Lightroom"))).ShouldBe(new RuleOutcome.Match(Category.Provenance, "webp-exif", "Software tag, camera model"));
}

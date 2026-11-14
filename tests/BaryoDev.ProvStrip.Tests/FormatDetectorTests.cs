namespace BaryoDev.ProvStrip.Tests;

public class FormatDetectorTests
{
    [Fact] public void Png() => FormatDetector.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]).ShouldBe("png");
    [Fact] public void Jpeg() => FormatDetector.Detect([0xFF, 0xD8, 0xFF, 0xE0]).ShouldBe("jpeg");
    [Fact] public void Webp() => FormatDetector.Detect("RIFF\0\0\0\0WEBPVP8 "u8).ShouldBe("webp");
    [Fact] public void SvgWithXmlDeclaration() => FormatDetector.Detect("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8).ShouldBe("svg");
    [Fact] public void SvgWithBom() => FormatDetector.Detect([0xEF, 0xBB, 0xBF, .. "<svg/>"u8]).ShouldBe("svg");
    [Fact] public void SvgAfterCommentAndDoctype() => FormatDetector.Detect("<!-- c --><!DOCTYPE svg><svg/>"u8).ShouldBe("svg");
    [Fact] public void SvgAfterALongComment() => FormatDetector.Detect(System.Text.Encoding.UTF8.GetBytes($"<!--{new string('x', 5000)}--><svg/>")).ShouldBe("svg");
    [Fact] public void SvgAfterALongDoctype() => FormatDetector.Detect(System.Text.Encoding.UTF8.GetBytes($"<!DOCTYPE svg [<!-- {new string('x', 5000)} -->]><svg/>")).ShouldBe("svg");
    [Fact] public void Utf16SvgIsNotDetected() => FormatDetector.Detect([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("<svg/>")]).ShouldBeNull();
    [Fact] public void GifIsNotSupported() => FormatDetector.Detect("GIF89a"u8).ShouldBeNull();
    [Fact] public void RiffThatIsNotWebp() => FormatDetector.Detect("RIFF\0\0\0\0WAVEfmt "u8).ShouldBeNull();
    [Fact] public void HtmlIsNotSvg() => FormatDetector.Detect("<html><svg/></html>"u8).ShouldBeNull();
}

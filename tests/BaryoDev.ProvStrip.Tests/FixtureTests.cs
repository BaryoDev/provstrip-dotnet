namespace BaryoDev.ProvStrip.Tests;

public class FixtureTests
{
    [Fact] public void EveryListedFixtureExists() =>
        Fixtures.ManifestNames().ShouldAllBe(n => File.Exists(Fixtures.PathOf(n)));

    [Fact] public void EveryFixtureOnDiskIsListed() =>
        Fixtures.All().ShouldAllBe(n => Fixtures.ManifestNames().Contains(n));

    [Theory, InlineData("c2pa.png"), InlineData("c2pa.jpg"), InlineData("c2pa.webp")]
    public void SignedFixturesCarryTheFixtureClaimGenerator(string name) =>
        Fixtures.ContainsAny(name, "provstrip-fixtures").ShouldBeTrue();

    // An SVG manifest is base64 inside <c2pa:manifest>, so the name only shows once decoded.
    [Fact] public void SignedSvgCarriesTheFixtureClaimGenerator()
    {
        var text = System.Text.Encoding.UTF8.GetString(Fixtures.Read("c2pa.svg"));
        var match = System.Text.RegularExpressions.Regex.Match(text, "<c2pa:manifest>(.*?)</c2pa:manifest>", System.Text.RegularExpressions.RegexOptions.Singleline);
        System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(match.Groups[1].Value)).ShouldContain("provstrip-fixtures");
    }

    [Fact] public void NoFixtureNamesARealTool() =>
        Fixtures.All().ShouldAllBe(n => !Fixtures.ContainsAny(n,
            "Adobe Firefly", "DALL", "Midjourney", "ChatGPT", "OpenAI", "Claude", "Gemini", "Canva", "Adobe Photoshop"));
}

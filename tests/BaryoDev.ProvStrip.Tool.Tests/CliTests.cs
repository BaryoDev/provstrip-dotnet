using System.Text.Json;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Tool;

namespace BaryoDev.ProvStrip.Tests;

public class CliTests
{
    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact] public void StripInPlaceThenVerifyIsClean()
    {
        var p = Temp.Copy("c2pa.png");
        Run("strip", "-i", p).Code.ShouldBe(0);
        Run("verify", p).ShouldSatisfyAllConditions(r => r.Code.ShouldBe(0), r => r.Out.ShouldContain("clean"));
    }

    [Fact] public void VerifyDirtyExitsTwo() => Run("verify", Fixtures.PathOf("c2pa.jpg")).Code.ShouldBe(2);

    [Fact] public void CameraFlagExitsOneAndKeepsTheFile()
    {
        var p = Temp.Copy("camera.jpg");
        var before = File.ReadAllBytes(p);
        Run("strip", "-i", p, "--camera", "flag").ShouldSatisfyAllConditions(r => r.Code.ShouldBe(1), r => r.Out.ShouldContain("GPS"));
        File.ReadAllBytes(p).ShouldBe(before);
    }

    [Fact] public void UnknownExitsTwoWithoutKeepUnknown() =>
        Run("strip", "-i", Temp.Copy("unknown-ancillary.png")).ShouldSatisfyAllConditions(
            r => r.Code.ShouldBe(2), r => r.Out.ShouldContain("unknown prVt segment at byte"), r => r.Out.ShouldContain("--keep-unknown"));

    [Fact] public void KeepUnknownExitsZero() => Run("strip", "-i", Temp.Copy("unknown-ancillary.png"), "--keep-unknown").Code.ShouldBe(0);

    [Fact] public void DirectoryIsWalked()
    {
        var d = Temp.Dir("c2pa.png", "c2pa.jpg", "clean.webp");
        Run("strip", "-i", d).Code.ShouldBe(0);
        Run("verify", d).Code.ShouldBe(0);
    }

    [Fact] public void TwoInputsWithoutInPlaceIsUsageError() => Run("strip", "a.png", "b.png", "c.png").Code.ShouldBe(64);

    [Fact] public void JsonIsParseable() =>
        JsonDocument.Parse(Run("verify", Fixtures.PathOf("c2pa.png"), "--json").Out).RootElement.GetProperty("files").GetArrayLength().ShouldBe(1);

    [Fact] public void JsonNamesEachFinding()
    {
        var file = JsonDocument.Parse(Run("verify", Fixtures.PathOf("c2pa.png"), "--json").Out).RootElement.GetProperty("files")[0];
        file.GetProperty("format").GetString().ShouldBe("png");
        file.GetProperty("flagged").EnumerateArray().ShouldContain(f => f.GetProperty("ruleId").GetString() == "png-cabx");
        file.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact] public void StripToAnOutputLeavesTheInput()
    {
        var p = Temp.Copy("c2pa.png");
        var before = File.ReadAllBytes(p);
        var output = Path.Combine(Path.GetDirectoryName(p)!, "clean.png");
        Run("strip", p, output).Code.ShouldBe(0);
        File.ReadAllBytes(p).ShouldBe(before);
        ProvStripEngine.Inspect(File.ReadAllBytes(output)).IsClean.ShouldBeTrue();
    }

    [Fact] public void StrippedLineNamesWhatWent() =>
        Run("strip", "-i", Temp.Copy("c2pa.png")).Out.ShouldContain("stripped C2PA");

    [Fact] public void GlobIsExpanded()
    {
        var d = Temp.Dir("c2pa.png", "c2pa.jpg");
        Run("strip", "-i", Path.Combine(d, "*.png")).Code.ShouldBe(0);
        Run("verify", Path.Combine(d, "c2pa.png")).Code.ShouldBe(0);
        Run("verify", Path.Combine(d, "c2pa.jpg")).Code.ShouldBe(2);
    }

    [Fact] public void BrokenFileExitsTwo() => Run("verify", Fixtures.PathOf("bad-crc.png")).Code.ShouldBe(2);

    [Fact] public void UnknownOptionIsUsageError() => Run("strip", "--nope", "a.png").Code.ShouldBe(64);

    [Fact] public void BadModeIsUsageError() => Run("strip", "-i", "a.png", "--camera", "maybe").Code.ShouldBe(64);

    [Fact] public void VersionPrintsTheVersion() => Run("--version").ShouldSatisfyAllConditions(r => r.Code.ShouldBe(0), r => r.Out.Trim().ShouldBe("0.1.0"));

    [Fact] public void HelpExitsZero() => Run("--help").ShouldSatisfyAllConditions(r => r.Code.ShouldBe(0), r => r.Out.ShouldContain("provstrip strip"));
}

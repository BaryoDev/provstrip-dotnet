using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Tests;

public class EngineTests
{
    static byte[] PngExifPayload(byte[] png)
    {
        var c = new PngContainer();
        var exif = c.ReadSegments(png).Single(s => s.Label == "eXIf");
        return c.Payload(exif, png).ToArray();
    }

    [Fact] public void C2paPngIsProvenance() =>
        ProvStripEngine.Inspect(Fixtures.Read("c2pa.png")).Findings.ShouldContain(f => f.Category == Category.Provenance && f.Description == "C2PA");

    [Fact] public void FindingsCarryFormatAndCanDelete() =>
        ProvStripEngine.Inspect(Fixtures.Read("c2pa.png")).Findings.ShouldAllBe(f => f.Format == "png" && f.CanDelete);

    [Fact] public void CleanPngIsClean() => ProvStripEngine.Inspect(Fixtures.Read("clean.png")).IsClean.ShouldBeTrue();

    [Fact] public void StripRemovesC2paAndVerifiesClean()
    {
        var input = Fixtures.Read("c2pa.png");
        var result = ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings);
        result.Changed.ShouldBeTrue();
        ProvStripEngine.Inspect(result.Output).IsClean.ShouldBeTrue();
    }

    [Fact] public void CameraStripKeepsOrientation()
    {
        var input = Fixtures.Read("camera.png");
        var output = ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings).Output;
        ProvStripEngine.Inspect(output).IsClean.ShouldBeTrue();
        Exif.Read(PngExifPayload(output)).Orientation.ShouldBe((ushort)6);
    }

    [Fact] public void StripReportsWhatRemains()
    {
        var input = Fixtures.Read("tool-xmp.png");
        var findings = ProvStripEngine.Inspect(input).Findings;
        var result = ProvStripEngine.Strip(input, [findings[0]]);
        result.Removed.ShouldBe([findings[0]]);
        result.Remaining.Select(f => f.RuleId).ShouldBe([findings[1].RuleId]);
    }

    [Fact] public void FlagByDefault()
    {
        var d = new StripPolicy().Decide(ProvStripEngine.Inspect(Fixtures.Read("tool-xmp.png")));
        d.Remove.ShouldBeEmpty();
        d.Flagged.Count.ShouldBe(2);
    }

    [Fact] public void AutoProvenanceFlagCamera()
    {
        var d = new StripPolicy(Provenance: CategoryMode.Auto).Decide(ProvStripEngine.Inspect(Fixtures.Read("camera.png")));
        d.Remove.ShouldBeEmpty();
        d.Flagged.ShouldAllBe(f => f.Category == Category.Camera);
        d.Flagged.ShouldNotBeEmpty();
    }

    [Fact] public void AutoBothRemovesBoth() =>
        new StripPolicy(CategoryMode.Auto, CategoryMode.Auto).Decide(ProvStripEngine.Inspect(Fixtures.Read("tool-xmp.png"))).Remove.Count.ShouldBe(2);

    [Fact] public void UnknownBlocksUnlessKept()
    {
        var i = ProvStripEngine.Inspect(Fixtures.Read("unknown-ancillary.png"));
        i.Findings.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Category.ShouldBe(Category.Unknown),
            f => f.Description.ShouldBe("unknown prVt segment"),
            f => f.CanDelete.ShouldBeTrue());
        new StripPolicy(CategoryMode.Auto, CategoryMode.Auto).Decide(i).Blocked.ShouldBeTrue();
        new StripPolicy(CategoryMode.Auto, CategoryMode.Auto, KeepUnknown: true).Decide(i).Blocked.ShouldBeFalse();
    }

    [Fact] public void UnknownIsNeverAutoRemoved() =>
        new StripPolicy(CategoryMode.Auto, CategoryMode.Auto, KeepUnknown: true)
            .Decide(ProvStripEngine.Inspect(Fixtures.Read("unknown-ancillary.png"))).Remove.ShouldBeEmpty();

    [Fact] public void SafeUnknownCanBeDeletedByHand()
    {
        var input = Fixtures.Read("unknown-ancillary.png");
        var output = ProvStripEngine.Strip(input, ProvStripEngine.Inspect(input).Findings).Output;
        output.ShouldBe(Fixtures.Read("clean.png"));
    }

    [Fact] public void CriticalUnknownCannotBeDeleted()
    {
        var input = Fixtures.Read("unknown-critical.png");
        var finding = ProvStripEngine.Inspect(input).Findings.ShouldHaveSingleItem();
        finding.CanDelete.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => ProvStripEngine.Strip(input, [finding]));
    }

    [Fact] public void FindingFromAnotherFileIsRejected() =>
        Should.Throw<ArgumentException>(() => ProvStripEngine.Strip(Fixtures.Read("clean.png"), ProvStripEngine.Inspect(Fixtures.Read("c2pa.png")).Findings));

    [Fact] public void NothingToRemoveReturnsSameBytes() =>
        ProvStripEngine.Strip(Fixtures.Read("clean.png"), []).Output.ShouldBe(Fixtures.Read("clean.png"));

    [Fact] public void UnsupportedFormatThrows() =>
        Should.Throw<UnsupportedFormatException>(() => ProvStripEngine.Inspect("GIF89a"u8.ToArray()));

    [Fact] public void CustomRulesAreUsed()
    {
        var rules = RuleSet.Parse("""{"provenanceMarkers":[],"standardIccDescriptions":[],"rules":[{"id":"mine","format":"png","label":"prVt","category":"camera","describe":"private"}]}""");
        ProvStripEngine.Inspect(Fixtures.Read("unknown-ancillary.png"), rules).Findings.ShouldHaveSingleItem().RuleId.ShouldBe("mine");
    }

    [Fact] public void PngTrailerIsUnknownAndStrippable()
    {
        var clean = Fixtures.Read("clean.png");
        byte[] input = [.. clean, .. "hidden"u8];
        var finding = ProvStripEngine.Inspect(input).Findings.ShouldHaveSingleItem();
        finding.Description.ShouldBe("unknown TRAILER segment");
        ProvStripEngine.Strip(input, [finding]).Output.ShouldBe(clean);
    }
}

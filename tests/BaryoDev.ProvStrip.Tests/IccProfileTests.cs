using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Tests;

public class IccProfileTests
{
    static RuleOutcome ClassifyIcc(string name)
    {
        var payload = Bytes.JpegPayload(Fixtures.Read(name), 0xE2, "ICC_PROFILE");
        return RuleSet.Default.Classify("jpeg", new Segment(0, payload.Length, "APP2", "ICC_PROFILE"), payload);
    }

    [Fact] public void ReadsTheV2Description() =>
        IccProfile.Description(Bytes.JpegPayload(Fixtures.Read("icc-machine.jpg"), 0xE2, "ICC_PROFILE")[14..]).ShouldBe("Fixture Monitor 27");

    [Fact] public void GarbageHasNoDescription() => IccProfile.Description(new byte[40]).ShouldBeNull();

    [Fact] public void StandardProfileIsKept() => ClassifyIcc("icc-standard.jpg").ShouldBe(RuleOutcome.Keep);

    [Fact] public void MachineProfileIsCamera() =>
        ClassifyIcc("icc-machine.jpg").ShouldBe(new RuleOutcome.Match(Category.Camera, "jpeg-icc", "colour profile \"Fixture Monitor 27\""));

    [Fact] public void PngIccpIsDecompressedFirst()
    {
        var profile = Bytes.JpegPayload(Fixtures.Read("icc-machine.jpg"), 0xE2, "ICC_PROFILE")[14..];
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(profile);
        byte[] payload = [.. "icc\0"u8, 0, .. ms.ToArray()];
        RuleSet.Default.Classify("png", new Segment(0, payload.Length, "iCCP", "icc"), payload)
            .ShouldBe(new RuleOutcome.Match(Category.Camera, "png-icc", "colour profile \"Fixture Monitor 27\""));
    }
}

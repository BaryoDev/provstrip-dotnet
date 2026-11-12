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

    // A PNG iCCP chunk is a zlib stream. A small one can inflate to gigabytes, so reading stops at
    // a cap and the profile counts as unreadable.
    [Fact] public void DecompressionStopsAtTheCap()
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (var i = 0; i < 64; i++) z.Write(zeros);
        }
        byte[] payload = [.. "icc\0"u8, 0, .. ms.ToArray()];
        (IccProfile.FromPayload("png", payload) is null).ShouldBeTrue("a 64 MB profile must be refused, not returned");
    }

    // A tag table whose one entry is "desc" at the given offset and size, followed by the tag bytes.
    static byte[] ProfileWithDesc(uint offset, uint size, byte[] tag)
    {
        var profile = new byte[144 + tag.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), 1);
        "desc"u8.CopyTo(profile.AsSpan(132));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136), offset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(140), size);
        tag.CopyTo(profile.AsSpan(144));
        return profile;
    }

    // Offsets and lengths near int.MaxValue used to wrap the bounds checks and escape as ArgumentOutOfRangeException.
    [Fact] public void DescTagPastIntMaxIsUnreadable() =>
        IccProfile.Description(ProfileWithDesc(0x40000000, 0x40000000, [])).ShouldBeNull();

    [Fact] public void DescTextLengthPastIntMaxIsUnreadable()
    {
        byte[] tag = [.. "desc"u8, 0, 0, 0, 0, 0x7F, 0xFF, 0xFF, 0xF8, (byte)'a', 0, 0, 0];
        IccProfile.Description(ProfileWithDesc(144, (uint)tag.Length, tag)).ShouldBeNull();
    }

    [Fact] public void MlucRecordPastIntMaxIsUnreadable()
    {
        // mluc, reserved, 1 record, record size 12, "enUS", length 0x20, offset 0x7FFFFFF0
        byte[] tag = [.. "mluc"u8, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 12, .. "enUS"u8, 0, 0, 0, 0x20, 0x7F, 0xFF, 0xFF, 0xF0];
        IccProfile.Description(ProfileWithDesc(144, (uint)tag.Length, tag)).ShouldBeNull();
    }

    [Fact] public void CorruptZlibIsUnreadable() => IccProfile.FromPayload("png", [.. "icc\0"u8, 0, 1, 2, 3, 4]).ShouldBeNull();
}

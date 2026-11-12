using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Tests;

public class ExifTests
{
    static byte[] JpegExifPayload(string name) => Bytes.JpegPayload(Fixtures.Read(name), 0xE1, "Exif");

    static RuleOutcome ClassifyExif(string name)
    {
        var payload = JpegExifPayload(name);
        return RuleSet.Default.Classify("jpeg", new Segment(0, payload.Length, "APP1", "Exif"), payload);
    }

    // Little-endian TIFF, IFD0 with one ASCII Model entry ("M"), no orientation.
    static byte[] ExifWithOnlyModel() =>
        [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x10, 0x01, 2, 0, 2, 0, 0, 0, (byte)'M', 0, 0, 0, 0, 0, 0, 0];

    static byte[] BigEndianExif(ushort orientation) =>
        [.. "MM\0*"u8, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0];

    static byte[] ExifWithIfdOffset(uint offset) =>
        [.. "II*\0"u8, (byte)offset, (byte)(offset >> 8), (byte)(offset >> 16), (byte)(offset >> 24)];

    [Fact] public void CameraFixtureSummary() =>
        Exif.Read(JpegExifPayload("camera.jpg")).ShouldBe(new ExifSummary(6, true, true, true, false));

    [Fact] public void OrientationOnlyKeepsSix()
    {
        var rewritten = Exif.OrientationOnly(JpegExifPayload("camera.jpg"))!;
        Exif.Read(rewritten).ShouldBe(new ExifSummary(6, false, false, false, true));
    }

    [Fact] public void OrientationOnlyKeepsTheExifPrefix() =>
        Exif.OrientationOnly(JpegExifPayload("camera.jpg"))![..6].ShouldBe("Exif\0\0"u8.ToArray());

    [Fact] public void NoOrientationGivesNull() => Exif.OrientationOnly(ExifWithOnlyModel()).ShouldBeNull();

    [Fact] public void BigEndianIsPreserved()
    {
        var rewritten = Exif.OrientationOnly(BigEndianExif(orientation: 3))!;
        rewritten[..2].ShouldBe("MM"u8.ToArray());
        Exif.Read(rewritten).Orientation.ShouldBe((ushort)3);
    }

    [Fact] public void OffsetOutsidePayloadThrows() => Should.Throw<ProvStripFormatException>(() => Exif.Read(ExifWithIfdOffset(0xFFFF)));

    // Offsets near int.MaxValue used to wrap the bounds check and escape as ArgumentOutOfRangeException.
    [Fact] public void IfdOffsetNearIntMaxThrows() => Should.Throw<ProvStripFormatException>(() => Exif.Read(ExifWithIfdOffset(0x7FFFFFFE)));

    [Fact] public void ExifIfdOffsetNearIntMaxThrows()
    {
        // II*\0, IFD0 at 8, one entry: tag 0x8769 (ExifIFD pointer) LONG 1 = 0x7FFFFFFE, next IFD 0.
        byte[] tiff = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x69, 0x87, 4, 0, 1, 0, 0, 0, 0xFE, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0];
        Should.Throw<ProvStripFormatException>(() => Exif.Read(tiff));
    }

    [Fact] public void NotTiffThrows() => Should.Throw<ProvStripFormatException>(() => Exif.Read("Exif\0\0nope"u8.ToArray()));

    [Fact] public void CameraDescription() =>
        ClassifyExif("camera.jpg").ShouldBe(new RuleOutcome.Match(Category.Camera, "jpeg-exif", "GPS, camera model, date taken"));

    [Fact] public void OnlyAModelIsDescribedAsCameraModel()
    {
        var payload = ExifWithOnlyModel();
        RuleSet.Default.Classify("webp", new Segment(0, payload.Length, "EXIF"), payload)
            .ShouldBe(new RuleOutcome.Match(Category.Camera, "webp-exif", "camera model"));
    }

    [Fact] public void OrientationOnlyFixtureIsKept() => ClassifyExif("orientation-only.jpg").ShouldBe(RuleOutcome.Keep);
}

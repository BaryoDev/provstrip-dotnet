using System.Buffers.Binary;

namespace BaryoDev.ProvStrip.Rules;

internal sealed record ExifSummary(ushort? Orientation, bool HasGps, bool HasCameraModel, bool HasDateTaken, bool OnlyOrientation);

/// <summary>Reads just enough of an EXIF (TIFF) block to describe it and to keep orientation.</summary>
internal static class Exif
{
    const ushort OrientationTag = 0x0112;

    static ReadOnlySpan<byte> Prefix => "Exif\0\0"u8;

    public static ExifSummary Read(ReadOnlySpan<byte> payload)
    {
        var tiff = WithoutPrefix(payload);
        var big = ByteOrder(tiff);
        var ifd0 = ReadU32(tiff, 4, big);

        ushort? orientation = null;
        bool gps = false, model = false, date = false;
        uint exifIfd = 0;

        var count = ReadU16(tiff, (int)ifd0, big);
        var entries = (int)ifd0 + 2;
        for (var i = 0; i < count; i++)
        {
            var at = entries + i * 12;
            var tag = ReadU16(tiff, at, big);
            switch (tag)
            {
                case OrientationTag: orientation = ReadU16(tiff, at + 8, big); break;
                case 0x010F or 0x0110: model = true; break;
                case 0x0132: date = true; break;
                case 0x8825: gps = true; break;
                case 0x8769: exifIfd = ReadU32(tiff, at + 8, big); break;
            }
        }
        var next = ReadU32(tiff, entries + count * 12, big);

        if (exifIfd != 0)
        {
            var n = ReadU16(tiff, (int)exifIfd, big);
            for (var i = 0; i < n; i++)
                if (ReadU16(tiff, (int)exifIfd + 2 + i * 12, big) is 0x9003 or 0x9004) date = true;
        }

        var only = count == 1 && orientation is not null && next == 0;
        return new ExifSummary(orientation, gps, model, date, only);
    }

    /// <summary>
    /// A new EXIF block holding only the orientation tag, in the same byte order and with the same
    /// "Exif\0\0" prefix if the original had one. Null when there is no orientation to keep.
    /// </summary>
    public static byte[]? OrientationOnly(ReadOnlySpan<byte> payload)
    {
        var summary = Read(payload);
        if (summary.Orientation is not { } value) return null;

        var hasPrefix = payload.StartsWith(Prefix);
        var big = ByteOrder(WithoutPrefix(payload));
        var tiff = new byte[26];
        tiff[0] = tiff[1] = big ? (byte)'M' : (byte)'I';
        Write16(tiff, 2, 42, big);
        Write32(tiff, 4, 8, big);
        Write16(tiff, 8, 1, big);          // one entry
        Write16(tiff, 10, OrientationTag, big);
        Write16(tiff, 12, 3, big);         // SHORT
        Write32(tiff, 14, 1, big);         // count 1
        Write16(tiff, 18, value, big);     // value, left-justified in the 4-byte field
        // next IFD offset (bytes 22-25) stays 0
        return hasPrefix ? [.. Prefix, .. tiff] : tiff;
    }

    /// <summary>What the block holds, for a label: "GPS, camera model, date taken".</summary>
    public static string Describe(ExifSummary s)
    {
        var parts = new List<string>(3);
        if (s.HasGps) parts.Add("GPS");
        if (s.HasCameraModel) parts.Add("camera model");
        if (s.HasDateTaken) parts.Add("date taken");
        return parts.Count == 0 ? "EXIF" : string.Join(", ", parts);
    }

    static ReadOnlySpan<byte> WithoutPrefix(ReadOnlySpan<byte> payload) =>
        payload.StartsWith(Prefix) ? payload[Prefix.Length..] : payload;

    static bool ByteOrder(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) throw new ProvStripFormatException("EXIF block is too short", 0);
        if (tiff[0] == 'I' && tiff[1] == 'I' && tiff[2] == 42 && tiff[3] == 0) return false;
        if (tiff[0] == 'M' && tiff[1] == 'M' && tiff[2] == 0 && tiff[3] == 42) return true;
        throw new ProvStripFormatException("EXIF block has no TIFF header", 0);
    }

    static ushort ReadU16(ReadOnlySpan<byte> b, int at, bool big)
    {
        if (at < 0 || at + 2 > b.Length) throw new ProvStripFormatException("EXIF offset points outside the block", at);
        return big ? BinaryPrimitives.ReadUInt16BigEndian(b[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    }

    static uint ReadU32(ReadOnlySpan<byte> b, int at, bool big)
    {
        if (at < 0 || at + 4 > b.Length) throw new ProvStripFormatException("EXIF offset points outside the block", at);
        return big ? BinaryPrimitives.ReadUInt32BigEndian(b[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    }

    static void Write16(byte[] b, int at, ushort v, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    }

    static void Write32(byte[] b, int at, uint v, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    }
}

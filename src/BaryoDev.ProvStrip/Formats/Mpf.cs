using System.Buffers.Binary;

namespace BaryoDev.ProvStrip.Formats;

/// <summary>
/// Checks that an APP2 MPF payload is an MP index and nothing else: a TIFF header, the index IFD, an optional
/// attribute IFD, and the values they point at, covering every byte with no gaps and no unknown tags.
/// </summary>
internal static class Mpf
{
    // ImageUIDList (0xB003) is left out: 33 free bytes per image is room for anything, so it is not structure.
    static bool IsIndexTag(ushort tag) => tag is 0xB000 or 0xB001 or 0xB002 or 0xB004;

    static bool IsAttributeTag(ushort tag) => tag is 0xB000 or 0xB101 or >= 0xB201 and <= 0xB20D;

    // The one type and count each tag has (CIPA DC-007), so no value can be stretched to carry other bytes.
    static bool Fits(ushort tag, ushort type, uint count, uint images) => tag switch
    {
        0xB000 => type == 7 && count == 4,
        0xB001 or 0xB004 or 0xB101 or 0xB201 or 0xB204 => type == 4 && count == 1,
        0xB002 => type == 7 && images > 0 && count == 16L * images,
        >= 0xB202 and <= 0xB20D => type is 5 or 10 && count == 1,
        _ => false,
    };

    public static bool IsExact(ReadOnlySpan<byte> payload)
    {
        if (!payload.StartsWith("MPF\0"u8)) return false;
        var tiff = payload[4..];
        if (tiff.Length < 8) return false;
        bool big;
        if (tiff[..4].SequenceEqual("II*\0"u8)) big = false;
        else if (tiff[..4].SequenceEqual("MM\0*"u8)) big = true;
        else return false;

        var regions = new List<(long Start, long End)> { (0, 8) };
        long ifd = U32(tiff, 4, big);
        uint images = 0;
        var next = ReadIfd(tiff, ifd, big, IsIndexTag, regions, ref images);
        if (next is null) return false;
        if (next != 0 && ReadIfd(tiff, next.Value, big, IsAttributeTag, regions, ref images) is not 0) return false;

        // The regions must tile the payload: a gap is bytes no structure accounts for, an overlap is a value
        // read two ways.
        regions.Sort();
        long covered = 0;
        foreach (var (start, end) in regions)
        {
            if (start != covered) return false;
            covered = end;
        }
        return covered == tiff.Length;
    }

    // Adds the IFD and its out-of-line values to regions. Returns the next IFD offset, or null when malformed.
    static long? ReadIfd(ReadOnlySpan<byte> tiff, long at, bool big, Func<ushort, bool> allowed, List<(long, long)> regions, ref uint images)
    {
        if (at < 8 || at + 2 > tiff.Length) return null;
        var count = U16(tiff, at, big);
        var end = at + 2 + count * 12L + 4;
        if (end > tiff.Length) return null;
        regions.Add((at, end));
        for (var i = 0; i < count; i++)
        {
            var entry = at + 2 + i * 12L;
            var tag = U16(tiff, entry, big);
            var type = U16(tiff, entry + 2, big);
            var values = U32(tiff, entry + 4, big);
            if (!allowed(tag) || !Fits(tag, type, values, images)) return null;
            if (tag == 0xB000 && !tiff.Slice((int)entry + 8, 4).SequenceEqual("0100"u8)) return null;
            if (tag == 0xB001) images = U32(tiff, entry + 8, big);
            var size = type switch
            {
                1 or 2 or 7 => 1L,
                3 => 2L,
                4 or 9 => 4L,
                5 or 10 => 8L,
                _ => -1L,
            };
            if (size < 0) return null;
            var length = size * values;
            if (length <= 4) continue;
            long offset = U32(tiff, entry + 8, big);
            if (offset + length > tiff.Length) return null;
            regions.Add((offset, offset + length));
        }
        return U32(tiff, end - 4, big);
    }

    static ushort U16(ReadOnlySpan<byte> b, long at, bool big) =>
        big ? BinaryPrimitives.ReadUInt16BigEndian(b[(int)at..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[(int)at..]);

    static uint U32(ReadOnlySpan<byte> b, long at, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(b[(int)at..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[(int)at..]);
}

using System.Buffers.Binary;
using System.Text;

namespace BaryoDev.ProvStrip.Formats;

/// <summary>PNG chunk walk. Every chunk is one segment: length, type, data and CRC.</summary>
internal sealed class PngContainer : IContainer
{
    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Needed to decode the image or to show it as intended. iCCP is not here: the rules decide it.
    static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "IHDR", "IDAT", "IEND", "PLTE", "tRNS",
        "gAMA", "cHRM", "sRGB", "sBIT", "cICP", "mDCv", "cLLi",
        "bKGD", "hIST", "pHYs", "sPLT", "acTL", "fcTL", "fdAT",
    };

    public string Format => "png";

    public bool Sniff(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Signature);

    public IReadOnlyList<Segment> ReadSegments(ReadOnlyMemory<byte> memory)
    {
        var bytes = memory.Span;
        if (!Sniff(bytes)) throw new ProvStripFormatException("not a PNG (bad signature)", 0);

        var segments = new List<Segment>();
        var i = 8;
        while (i < bytes.Length)
        {
            if (i + 12 > bytes.Length) throw new ProvStripFormatException("PNG truncated", i);
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]);
            if (length > int.MaxValue - 12 || i + 12 + (long)length > bytes.Length)
                throw new ProvStripFormatException("PNG truncated", i);

            var type = Encoding.ASCII.GetString(bytes.Slice(i + 4, 4));
            var data = bytes.Slice(i + 8, (int)length);
            var stored = BinaryPrimitives.ReadUInt32BigEndian(bytes[(i + 8 + (int)length)..]);
            if (stored != Crc32(bytes.Slice(i + 4, 4 + (int)length)))
                throw new ProvStripFormatException($"CRC mismatch on {type} chunk", i);

            segments.Add(new Segment(i, 12 + (int)length, type, KeywordOf(type, data)));
            i += 12 + (int)length;
            if (type == "IEND") break;
        }

        if (segments.Count == 0 || segments[^1].Label != "IEND")
            throw new ProvStripFormatException("PNG has no IEND chunk", bytes.Length);
        return segments;
    }

    public bool IsStructural(Segment segment, ReadOnlySpan<byte> bytes) => Structural.Contains(segment.Label);

    // Ancillary chunks (lowercase first letter) can go; a decoder must understand critical ones.
    public bool IsSafeToDrop(Segment segment) => segment.Label.Length == 4 && char.IsLower(segment.Label[0]);

    public ReadOnlyMemory<byte> Payload(Segment segment, ReadOnlyMemory<byte> bytes) =>
        bytes.Slice(segment.Start + 8, segment.Length - 12);

    public byte[] Rewrite(ReadOnlyMemory<byte> memory, IReadOnlyList<SegmentEdit> edits)
    {
        var bytes = memory.Span;
        var byStart = edits.ToDictionary(e => e.Segment.Start);
        using var output = new MemoryStream(bytes.Length);
        output.Write(bytes[..8]);

        Span<byte> head = stackalloc byte[8];
        Span<byte> crc = stackalloc byte[4];
        var end = 8;
        foreach (var segment in ReadSegments(memory))
        {
            end = segment.End;
            if (!byStart.TryGetValue(segment.Start, out var edit))
            {
                output.Write(bytes.Slice(segment.Start, segment.Length));
                continue;
            }
            if (edit.Replacement is not { } data) continue;

            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
            bytes.Slice(segment.Start + 4, 4).CopyTo(head[4..]);
            output.Write(head);
            output.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. head[4..], .. data]));
            output.Write(crc);
        }

        // Anything after IEND is not ours to judge, so it is copied as it was.
        output.Write(bytes[end..]);
        return output.ToArray();
    }

    static string? KeywordOf(string type, ReadOnlySpan<byte> data)
    {
        if (type is not ("tEXt" or "zTXt" or "iTXt" or "iCCP")) return null;
        var nul = data.IndexOf((byte)0);
        return nul < 0 ? null : Encoding.Latin1.GetString(data[..nul]);
    }

    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

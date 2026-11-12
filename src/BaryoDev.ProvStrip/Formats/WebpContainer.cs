using System.Buffers.Binary;
using System.Text;

namespace BaryoDev.ProvStrip.Formats;

/// <summary>RIFF chunk walk. Each segment is the 8-byte chunk header, the data and the pad byte when the size is odd.</summary>
internal sealed class WebpContainer : IContainer
{
    const string Trailer = "TRAILER";
    const byte ExifFlag = 0x08;
    const byte XmpFlag = 0x04;

    static readonly HashSet<string> Structural = new(StringComparer.Ordinal) { "VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF" };

    public string Format => "webp";

    public bool Sniff(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    public IReadOnlyList<Segment> ReadSegments(ReadOnlyMemory<byte> memory)
    {
        var bytes = memory.Span;
        if (!Sniff(bytes)) throw new ProvStripFormatException("not a WebP (no RIFF WEBP header)", 0);

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (riffSize < 4 || 8 + (long)riffSize > bytes.Length)
            throw new ProvStripFormatException("RIFF size is larger than the file", 4);
        var riffEnd = 8 + (int)riffSize;

        var segments = new List<Segment>();
        var i = 12;
        while (i < riffEnd)
        {
            if (i + 8 > riffEnd) throw new ProvStripFormatException("WebP chunk header runs past the RIFF", i);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]);
            var total = 8 + (long)size + (size & 1);
            if (i + total > riffEnd) throw new ProvStripFormatException("WebP chunk runs past the RIFF", i);
            segments.Add(new Segment(i, (int)total, Encoding.ASCII.GetString(bytes.Slice(i, 4))));
            i += (int)total;
        }

        // Bytes after the RIFF are no part of the image, but they can carry anything.
        if (riffEnd < bytes.Length) segments.Add(new Segment(riffEnd, bytes.Length - riffEnd, Trailer));
        return segments;
    }

    public bool IsStructural(Segment segment, ReadOnlySpan<byte> bytes) =>
        segment.Label == "ANMF" ? FrameHoldsOnlyImage(bytes.Slice(segment.Start + 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(segment.Start + 4)..]))) : Structural.Contains(segment.Label);

    // A frame is a 16-byte header and then chunks of its own. Only image data may sit inside one.
    static bool FrameHoldsOnlyImage(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 16) return false;
        var i = 16;
        while (i < frame.Length)
        {
            if (i + 8 > frame.Length) return false;
            var label = Encoding.ASCII.GetString(frame.Slice(i, 4));
            if (label is not ("ALPH" or "VP8 " or "VP8L")) return false;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(frame[(i + 4)..]);
            var total = 8 + (long)size + (size & 1);
            if (i + total > frame.Length) return false;
            i += (int)total;
        }
        return true;
    }

    public bool IsSafeToDrop(Segment segment) => !Structural.Contains(segment.Label);

    public ReadOnlyMemory<byte> Payload(Segment segment, ReadOnlyMemory<byte> bytes)
    {
        if (segment.Label == Trailer) return bytes.Slice(segment.Start, segment.Length);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span[(segment.Start + 4)..]);
        return bytes.Slice(segment.Start + 8, (int)size);
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> memory, IReadOnlyList<SegmentEdit> edits)
    {
        var bytes = memory.Span;
        var byStart = edits.ToDictionary(e => e.Segment.Start);
        var segments = ReadSegments(memory);
        bool Remains(string label) => segments.Any(s => s.Label == label
            && (!byStart.TryGetValue(s.Start, out var e) || e.Replacement is not null));

        using var output = new MemoryStream(bytes.Length);
        output.Write(bytes[..12]);
        var flagAt = -1;
        var riffEnd = -1;
        Span<byte> head = stackalloc byte[8];
        foreach (var segment in segments)
        {
            if (segment.Label == Trailer) riffEnd = (int)output.Length;
            if (!byStart.TryGetValue(segment.Start, out var edit))
            {
                if (segment.Label == "VP8X") flagAt = (int)output.Length + 8;
                output.Write(bytes.Slice(segment.Start, segment.Length));
                continue;
            }
            if (edit.Replacement is not { } data) continue;

            bytes.Slice(segment.Start, 4).CopyTo(head);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)data.Length);
            output.Write(head);
            output.Write(data);
            if ((data.Length & 1) == 1) output.WriteByte(0);
        }

        var result = output.ToArray();
        if (riffEnd < 0) riffEnd = result.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(riffEnd - 8));
        if (flagAt >= 0)
        {
            if (!Remains("EXIF")) result[flagAt] &= unchecked((byte)~ExifFlag);
            if (!Remains("XMP ")) result[flagAt] &= unchecked((byte)~XmpFlag);
        }
        return result;
    }
}

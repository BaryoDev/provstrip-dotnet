using System.Buffers.Binary;

namespace BaryoDev.ProvStrip.Formats;

/// <summary>
/// JPEG marker walk. Each marker and its length-counted payload is one segment; an SOS segment also holds the
/// entropy-coded data after it. Fill bytes (extra 0xFF before a marker) belong to the segment before them.
/// </summary>
internal sealed class JpegContainer : IContainer
{
    const string Trailer = "TRAILER";

    public string Format => "jpeg";

    public bool Sniff(ReadOnlySpan<byte> bytes) => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    public IReadOnlyList<Segment> ReadSegments(ReadOnlyMemory<byte> memory)
    {
        var bytes = memory.Span;
        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new ProvStripFormatException("not a JPEG (no SOI marker)", 0);

        var segments = new List<Segment> { new(0, 2, "SOI") };
        var i = 2;
        while (true)
        {
            // Fill bytes before a marker stay with the segment they follow.
            var fill = i;
            while (fill + 1 < bytes.Length && bytes[fill] == 0xFF && bytes[fill + 1] == 0xFF) fill++;
            if (fill > i)
            {
                segments[^1] = segments[^1] with { Length = segments[^1].Length + fill - i };
                i = fill;
            }

            if (i >= bytes.Length) throw new ProvStripFormatException("JPEG has no EOI marker", bytes.Length);
            if (i + 1 >= bytes.Length || bytes[i] != 0xFF)
                throw new ProvStripFormatException("expected a JPEG marker", i);

            var marker = bytes[i + 1];
            var label = LabelOf(marker);
            if (marker == 0xD9)
            {
                segments.Add(new Segment(i, 2, label));
                i += 2;
                break;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                segments.Add(new Segment(i, 2, label));
                i += 2;
                continue;
            }

            if (i + 4 > bytes.Length) throw new ProvStripFormatException("JPEG truncated", i);
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..]);
            if (length < 2 || i + 2 + length > bytes.Length)
                throw new ProvStripFormatException($"{label} length runs past the end", i);

            var end = i + 2 + length;
            if (marker == 0xDA) end = EndOfScan(bytes, end);
            segments.Add(new Segment(i, end - i, label, KeywordOf(marker, bytes.Slice(i + 4, length - 2))));
            i = end;
        }

        if (i < bytes.Length) segments.Add(new Segment(i, bytes.Length - i, Trailer));
        return segments;
    }

    // Entropy-coded data runs to the next marker that is not a stuffed zero, a restart marker or fill.
    static int EndOfScan(ReadOnlySpan<byte> bytes, int j)
    {
        while (j + 1 < bytes.Length)
        {
            if (bytes[j] != 0xFF) { j++; continue; }
            var b = bytes[j + 1];
            if (b == 0x00 || b is >= 0xD0 and <= 0xD7) { j += 2; continue; }
            if (b == 0xFF) { j++; continue; }
            return j;
        }
        throw new ProvStripFormatException("JPEG has no EOI marker", bytes.Length);
    }

    // A keyword alone is not enough: the fixed layouts are checked so no extra bytes ride along.
    public bool IsStructural(Segment segment, ReadOnlySpan<byte> bytes) => segment.Label switch
    {
        "SOI" or "DHT" or "DQT" or "DRI" or "DAC" or "DNL" or "SOS" or "EOI" => true,
        "APP0" => segment.Keyword == "JFIF" && IsExactJfif(PayloadSpan(segment, bytes)),
        "APP14" => segment.Keyword == "Adobe" && PayloadSpan(segment, bytes).Length == 12,
        "APP2" => segment.Keyword == "MPF",
        Trailer => HasMpf(bytes) && TrailerHoldsOnlyImages(bytes[segment.Start..segment.End]),
        var l => l.StartsWith("SOF", StringComparison.Ordinal),
    };

    // "JFIF\0", version 1.x, units 0 to 2, densities, and no thumbnail: a thumbnail is raw bytes no rule reads.
    static bool IsExactJfif(ReadOnlySpan<byte> payload) =>
        payload.Length == 14 && payload[4] == 0 && payload[5] == 1 && payload[7] <= 2 && payload[12] == 0 && payload[13] == 0;

    ReadOnlySpan<byte> PayloadSpan(Segment segment, ReadOnlySpan<byte> bytes) =>
        bytes.Slice(segment.Start + 4, BinaryPrimitives.ReadUInt16BigEndian(bytes[(segment.Start + 2)..]) - 2);

    bool HasMpf(ReadOnlySpan<byte> bytes) => ReadSegments(bytes.ToArray()).Any(s => s.Label == "APP2" && s.Keyword == "MPF");

    // The images after an MPF primary are JPEGs too, and can carry their own metadata. The trailer is kept only
    // when every one of them is nothing but image structure.
    bool TrailerHoldsOnlyImages(ReadOnlySpan<byte> trailer)
    {
        var rest = trailer.ToArray();
        while (rest.Length > 0)
        {
            if (!Sniff(rest)) return false;
            IReadOnlyList<Segment> segments;
            try
            {
                segments = ReadSegments(rest);
            }
            catch (ProvStripFormatException)
            {
                return false;
            }
            foreach (var s in segments)
                if (s.Label != Trailer && !IsStructural(s, rest)) return false;
            rest = segments[^1].Label == Trailer ? rest[segments[^1].Start..] : [];
        }
        return true;
    }

    public bool IsSafeToDrop(Segment segment) =>
        segment.Label is "COM" or Trailer || segment.Label.StartsWith("APP", StringComparison.Ordinal);

    public ReadOnlyMemory<byte> Payload(Segment segment, ReadOnlyMemory<byte> bytes)
    {
        if (segment.Label == Trailer) return bytes.Slice(segment.Start, segment.Length);
        var span = bytes.Span;
        var marker = span[segment.Start + 1];
        if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7) return ReadOnlyMemory<byte>.Empty;
        var length = BinaryPrimitives.ReadUInt16BigEndian(span[(segment.Start + 2)..]);
        return bytes.Slice(segment.Start + 4, length - 2);
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> memory, IReadOnlyList<SegmentEdit> edits)
    {
        var bytes = memory.Span;
        var byStart = edits.ToDictionary(e => e.Segment.Start);
        using var output = new MemoryStream(bytes.Length);
        Span<byte> head = stackalloc byte[4];
        foreach (var segment in ReadSegments(memory))
        {
            if (!byStart.TryGetValue(segment.Start, out var edit))
            {
                output.Write(bytes.Slice(segment.Start, segment.Length));
                continue;
            }
            if (edit.Replacement is not { } data) continue;
            if (data.Length > ushort.MaxValue - 2)
                throw new ProvStripException($"replacement for {segment.Label} is too large for one JPEG segment");

            head[0] = 0xFF;
            head[1] = bytes[segment.Start + 1];
            BinaryPrimitives.WriteUInt16BigEndian(head[2..], (ushort)(data.Length + 2));
            output.Write(head);
            output.Write(data);
        }
        return output.ToArray();
    }

    static string LabelOf(byte marker) => marker switch
    {
        0xD8 => "SOI",
        0xD9 => "EOI",
        0xDA => "SOS",
        0xC4 => "DHT",
        0xCC => "DAC",
        0xDB => "DQT",
        0xDD => "DRI",
        0xDC => "DNL",
        0xFE => "COM",
        >= 0xC0 and <= 0xCF => $"SOF{marker - 0xC0}",
        >= 0xE0 and <= 0xEF => $"APP{marker - 0xE0}",
        _ => $"M{marker:X2}",
    };

    // Leading printable ASCII up to the first NUL, at most 64 bytes. APP11 (JUMBF) is named by its first two bytes.
    static string? KeywordOf(byte marker, ReadOnlySpan<byte> payload)
    {
        if (marker == 0xEB) return payload.Length >= 2 ? System.Text.Encoding.ASCII.GetString(payload[..2]) : null;
        if (marker is not (>= 0xE0 and <= 0xEF or 0xFE)) return null;
        var head = payload[..Math.Min(payload.Length, 64)];
        var n = 0;
        while (n < head.Length && head[n] is >= 0x20 and < 0x7F) n++;
        return n == 0 ? null : System.Text.Encoding.ASCII.GetString(head[..n]);
    }
}

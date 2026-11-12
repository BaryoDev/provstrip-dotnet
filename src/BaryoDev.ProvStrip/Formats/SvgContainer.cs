using System.Text;

namespace BaryoDev.ProvStrip.Formats;

/// <summary>
/// A text scanner over the SVG's UTF-8 bytes, not a DOM, so nothing is reformatted. It knows comments, CDATA,
/// processing instructions, declarations and quoted attribute values, so markers inside them are not taken for tags.
/// Segments are metadata elements, elements and attributes in the c2pa, inkscape and sodipodi namespaces, their
/// xmlns declarations (labelled "prefix:xmlns") and comments. Segments may nest.
/// </summary>
internal sealed class SvgContainer : IContainer
{
    static readonly string[] Prefixes = ["c2pa", "inkscape", "sodipodi"];

    public string Format => "svg";

    public bool Sniff(ReadOnlySpan<byte> bytes) => FormatDetector.LooksLikeSvg(bytes);

    public IReadOnlyList<Segment> ReadSegments(ReadOnlyMemory<byte> memory)
    {
        var b = memory.Span;
        var segments = new List<Segment>();
        var open = new List<(string Name, int Start, string? Label)>();
        var i = 0;
        while (true)
        {
            var lt = b[i..].IndexOf((byte)'<');
            if (lt < 0) break;
            i += lt;

            if (b[i..].StartsWith("<!--"u8))
            {
                var end = After(b, i + 4, "-->"u8, "unclosed comment", i);
                segments.Add(new Segment(i, end - i, "#comment"));
                i = end;
            }
            else if (b[i..].StartsWith("<![CDATA["u8)) i = After(b, i + 9, "]]>"u8, "unclosed CDATA section", i);
            else if (b[i..].StartsWith("<?"u8)) i = After(b, i + 2, "?>"u8, "unclosed processing instruction", i);
            else if (b[i..].StartsWith("<!"u8)) i = SkipDeclaration(b, i);
            else if (b[i..].StartsWith("</"u8)) i = CloseTag(b, i, open, segments);
            else i = OpenTag(b, i, open, segments);
        }

        foreach (var o in open)
            if (o.Label is not null) throw new ProvStripFormatException($"unclosed <{o.Name}> element", o.Start);
        return segments.OrderBy(s => s.Start).ToList();
    }

    static int OpenTag(ReadOnlySpan<byte> b, int i, List<(string Name, int Start, string? Label)> open, List<Segment> segments)
    {
        var j = i + 1;
        while (j < b.Length && !IsSpace(b[j]) && b[j] is not ((byte)'>' or (byte)'/')) j++;
        var name = Encoding.UTF8.GetString(b[(i + 1)..j]);
        if (name.Length == 0) throw new ProvStripFormatException("malformed tag", i);

        var selfClosing = false;
        while (true)
        {
            var spaceStart = j;
            while (j < b.Length && IsSpace(b[j])) j++;
            if (j >= b.Length) throw new ProvStripFormatException($"unclosed <{name}> tag", i);
            if (b[j] == '>') { j++; break; }
            if (b[j] == '/' && j + 1 < b.Length && b[j + 1] == '>') { selfClosing = true; j += 2; break; }
            if (j == spaceStart) throw new ProvStripFormatException($"malformed attribute in <{name}>", j);

            var nameStart = j;
            while (j < b.Length && b[j] != '=' && !IsSpace(b[j]) && b[j] is not ((byte)'>' or (byte)'/')) j++;
            var attribute = Encoding.UTF8.GetString(b[nameStart..j]);
            while (j < b.Length && IsSpace(b[j])) j++;
            if (j >= b.Length || b[j] != '=') throw new ProvStripFormatException($"attribute {attribute} has no value", nameStart);
            j++;
            while (j < b.Length && IsSpace(b[j])) j++;
            if (j >= b.Length || b[j] is not ((byte)'"' or (byte)'\''))
                throw new ProvStripFormatException($"attribute {attribute} is not quoted", j);
            var close = b[(j + 1)..].IndexOf(b[j]);
            if (close < 0) throw new ProvStripFormatException($"unclosed value for {attribute}", j);
            j += close + 2;

            if (AttributeLabel(attribute) is { } label) segments.Add(new Segment(spaceStart, j - spaceStart, label));
        }

        var elementLabel = ElementLabel(name);
        if (selfClosing)
        {
            if (elementLabel is not null) segments.Add(new Segment(i, j - i, elementLabel));
        }
        else open.Add((name, i, elementLabel));
        return j;
    }

    static int CloseTag(ReadOnlySpan<byte> b, int i, List<(string Name, int Start, string? Label)> open, List<Segment> segments)
    {
        var gt = b[i..].IndexOf((byte)'>');
        if (gt < 0) throw new ProvStripFormatException("unclosed end tag", i);
        var end = i + gt + 1;
        var name = Encoding.UTF8.GetString(b[(i + 2)..(end - 1)]).Trim();

        var k = open.FindLastIndex(o => o.Name == name);
        if (k < 0) return end;
        for (var n = open.Count - 1; n > k; n--)
            if (open[n].Label is not null) throw new ProvStripFormatException($"unclosed <{open[n].Name}> element", open[n].Start);
        if (open[k].Label is { } label) segments.Add(new Segment(open[k].Start, end - open[k].Start, label));
        open.RemoveRange(k, open.Count - k);
        return end;
    }

    // A doctype may hold an internal subset in brackets, and quoted strings that contain '>'.
    static int SkipDeclaration(ReadOnlySpan<byte> b, int i)
    {
        var depth = 0;
        byte quote = 0;
        for (var j = i + 2; j < b.Length; j++)
        {
            var c = b[j];
            if (quote != 0) { if (c == quote) quote = 0; continue; }
            if (c is (byte)'"' or (byte)'\'') quote = c;
            else if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '>' && depth <= 0) return j + 1;
        }
        throw new ProvStripFormatException("unclosed declaration", i);
    }

    static int After(ReadOnlySpan<byte> b, int from, ReadOnlySpan<byte> close, string error, int start)
    {
        var at = b[from..].IndexOf(close);
        if (at < 0) throw new ProvStripFormatException(error, start);
        return from + at + close.Length;
    }

    static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    static string? Prefix(string name) => name.IndexOf(':') is var colon and > 0 ? name[..colon] : null;

    static string? ElementLabel(string name)
    {
        var prefix = Prefix(name);
        if (prefix is not null && Prefixes.Contains(prefix)) return name;
        return name == "metadata" || name.EndsWith(":metadata", StringComparison.Ordinal) ? "metadata" : null;
    }

    static string? AttributeLabel(string name)
    {
        if (name.StartsWith("xmlns:", StringComparison.Ordinal))
            return Prefixes.Contains(name[6..]) ? $"{name[6..]}:xmlns" : null;
        return Prefix(name) is { } prefix && Prefixes.Contains(prefix) ? name : null;
    }

    public bool IsStructural(Segment segment, ReadOnlySpan<byte> bytes) => false;

    public bool IsSafeToDrop(Segment segment) => true;

    // A comment no rule names is ordinary text, not an unknown.
    public bool KeepsUnmatched(Segment segment) => segment.Label == "#comment";

    public ReadOnlyMemory<byte> Payload(Segment segment, ReadOnlyMemory<byte> bytes) => bytes.Slice(segment.Start, segment.Length);

    public byte[] Rewrite(ReadOnlyMemory<byte> memory, IReadOnlyList<SegmentEdit> edits)
    {
        var bytes = memory.Span;
        using var output = new MemoryStream(bytes.Length);
        var at = 0;
        foreach (var edit in edits.OrderBy(e => e.Segment.Start).ThenByDescending(e => e.Segment.Length))
        {
            if (edit.Segment.Start < at) continue;
            output.Write(bytes[at..edit.Segment.Start]);
            if (edit.Replacement is { } data) output.Write(data);
            at = edit.Segment.End;
        }
        output.Write(bytes[at..]);
        return output.ToArray();
    }
}

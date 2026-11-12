using System.Text;
using System.Text.RegularExpressions;

namespace BaryoDev.ProvStrip.Formats;

/// <summary>
/// A text scanner over the SVG's UTF-8 bytes, not a DOM, so nothing is reformatted. It knows comments, CDATA,
/// processing instructions, the doctype and quoted attribute values, so markers inside them are not taken for tags.
/// Names are resolved through the xmlns declarations in scope, so a namespace is known by its URI, not its prefix.
/// </summary>
/// <remarks>
/// Segments: metadata elements; elements and attributes in the C2PA, Inkscape and Sodipodi namespaces (labelled
/// with that namespace's usual prefix) and their xmlns declarations (labelled "prefix:xmlns"); elements, attributes
/// and declarations in any other namespace except SVG, XLink and XML, as written (no rule names them, so they are
/// unknown); comments; processing instructions other than the XML declaration ("#pi"); and a doctype with an
/// internal subset ("#doctype"), which can define entities this scanner does not expand, so it cannot be dropped.
/// Inside a segment element, only the known namespaces make segments of their own.
/// </remarks>
internal sealed class SvgContainer : IContainer
{
    static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["http://c2pa.org/manifest"] = "c2pa",
        ["http://www.inkscape.org/namespaces/inkscape"] = "inkscape",
        ["http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd"] = "sodipodi",
    };

    static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "http://www.w3.org/2000/svg", "http://www.w3.org/1999/xlink", "http://www.w3.org/XML/1998/namespace",
    };

    sealed record Open(string Name, int Start, string? Label, Dictionary<string, string> Scope);

    public string Format => "svg";

    public bool Sniff(ReadOnlySpan<byte> bytes) => FormatDetector.LooksLikeSvg(bytes);

    public IReadOnlyList<Segment> ReadSegments(ReadOnlyMemory<byte> memory)
    {
        var b = memory.Span;
        var segments = new List<Segment>();
        var open = new List<Open>();
        var labelledOpen = 0;
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
            else if (b[i..].StartsWith("<?"u8))
            {
                var end = After(b, i + 2, "?>"u8, "unclosed processing instruction", i);
                var atStart = i == 0 || (i == 3 && b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
                if (!(atStart && IsPlainXmlDeclaration(Encoding.UTF8.GetString(b[i..end]), i)))
                    segments.Add(new Segment(i, end - i, "#pi"));
                i = end;
            }
            else if (b[i..].StartsWith("<!DOCTYPE"u8))
            {
                var (end, subset) = SkipDoctype(b, i);
                if (subset || !IsPlainDoctype(Encoding.UTF8.GetString(b[i..end])))
                    segments.Add(new Segment(i, end - i, "#doctype"));
                i = end;
            }
            else if (b[i..].StartsWith("<!"u8)) throw new ProvStripFormatException("unexpected declaration", i);
            else if (b[i..].StartsWith("</"u8)) i = CloseTag(b, i, open, segments, ref labelledOpen);
            else i = OpenTag(b, i, open, segments, ref labelledOpen);
        }

        if (open.Count > 0) throw new ProvStripFormatException($"unclosed <{open[^1].Name}> element", open[^1].Start);
        return segments.OrderBy(s => s.Start).ToList();
    }

    static int OpenTag(ReadOnlySpan<byte> b, int i, List<Open> open, List<Segment> segments, ref int labelledOpen)
    {
        var j = i + 1;
        while (j < b.Length && !IsSpace(b[j]) && b[j] is not ((byte)'>' or (byte)'/')) j++;
        var name = Encoding.UTF8.GetString(b[(i + 1)..j]);
        if (name.Length == 0) throw new ProvStripFormatException("malformed tag", i);

        var attributes = new List<(string Name, string Value, int Start, int End)>();
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
            var value = Encoding.UTF8.GetString(b.Slice(j + 1, close));
            j += close + 2;
            attributes.Add((attribute, value, spaceStart, j));
        }

        // This element's own declarations apply to its name and its attributes.
        var scope = open.Count > 0 ? open[^1].Scope : new Dictionary<string, string>(StringComparer.Ordinal);
        if (attributes.Any(a => a.Name == "xmlns" || a.Name.StartsWith("xmlns:", StringComparison.Ordinal)))
        {
            scope = new Dictionary<string, string>(scope, StringComparer.Ordinal);
            foreach (var a in attributes)
                if (a.Name == "xmlns") scope[""] = a.Value;
                else if (a.Name.StartsWith("xmlns:", StringComparison.Ordinal)) scope[a.Name[6..]] = a.Value;
        }

        var insideLabelled = labelledOpen > 0;
        var elementLabel = ElementLabel(name, scope, insideLabelled);
        foreach (var a in attributes)
            if (AttributeLabel(a.Name, a.Value, scope, insideLabelled || elementLabel is not null) is { } label)
                segments.Add(new Segment(a.Start, a.End - a.Start, label));

        if (selfClosing)
        {
            if (elementLabel is not null) segments.Add(new Segment(i, j - i, elementLabel));
        }
        else
        {
            open.Add(new Open(name, i, elementLabel, scope));
            if (elementLabel is not null) labelledOpen++;
        }
        return j;
    }

    static int CloseTag(ReadOnlySpan<byte> b, int i, List<Open> open, List<Segment> segments, ref int labelledOpen)
    {
        var gt = b[i..].IndexOf((byte)'>');
        if (gt < 0) throw new ProvStripFormatException("unclosed end tag", i);
        var end = i + gt + 1;
        var name = Encoding.UTF8.GetString(b[(i + 2)..(end - 1)]).Trim();

        if (open.Count == 0 || open[^1].Name != name)
            throw new ProvStripFormatException(
                open.Count == 0 ? $"</{name}> closes nothing" : $"</{name}> does not close <{open[^1].Name}>", i);
        var top = open[^1];
        open.RemoveAt(open.Count - 1);
        if (top.Label is { } label)
        {
            segments.Add(new Segment(top.Start, end - top.Start, label));
            labelledOpen--;
        }
        return end;
    }

    static string? Resolve(string? prefix, Dictionary<string, string> scope) =>
        prefix == "xml" ? "http://www.w3.org/XML/1998/namespace" : scope.GetValueOrDefault(prefix ?? "");

    static (string? Prefix, string Local) Split(string name) =>
        name.IndexOf(':') is var colon and > 0 ? (name[..colon], name[(colon + 1)..]) : (null, name);

    // Known namespaces are labelled with their usual prefix whatever prefix the file uses. An unbound c2pa,
    // inkscape or sodipodi prefix is taken at its word. Other namespaces are labelled as written.
    static string? NamespaceLabel(string name, string? uri, string? prefix, string local, bool insideLabelled)
    {
        if (uri is not null && Known.TryGetValue(uri, out var known)) return $"{known}:{local}";
        if (uri is null && prefix is not null && Known.ContainsValue(prefix)) return name;
        if (uri is null || Plain.Contains(uri)) return null;
        if (insideLabelled) return null;
        // A known prefix bound to some other URI must not be matched by that prefix's rules.
        return prefix is not null && Known.ContainsValue(prefix) ? $"{{{uri}}}{local}" : name;
    }

    static string? ElementLabel(string name, Dictionary<string, string> scope, bool insideLabelled)
    {
        var (prefix, local) = Split(name);
        var uri = Resolve(prefix, scope);
        if (local == "metadata" && (uri is null || uri == "http://www.w3.org/2000/svg")) return "metadata";
        return NamespaceLabel(name, uri, prefix, local, insideLabelled);
    }

    static string? AttributeLabel(string name, string value, Dictionary<string, string> scope, bool insideLabelled)
    {
        if (name == "xmlns") return null;
        if (name.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            if (Known.TryGetValue(value, out var known)) return $"{known}:xmlns";
            if (Plain.Contains(value) || insideLabelled) return null;
            return Known.ContainsValue(name[6..]) ? $"{{{value}}}xmlns" : name;
        }
        var (prefix, local) = Split(name);
        if (prefix is null) return null;
        return NamespaceLabel(name, Resolve(prefix, scope), prefix, local, insideLabelled);
    }

    // XML whitespace only: .NET's \s also matches Unicode spaces that XML does not allow.
    const string Ws = "[ \\t\\r\\n]";

    static readonly Regex XmlDeclaration = new(
        $"""^<\?xml((?:{Ws}+(?:version|encoding|standalone){Ws}*={Ws}*(?:"[^"]*"|'[^']*'))*){Ws}*\?>$""",
        RegexOptions.CultureInvariant);

    static readonly Regex PseudoAttribute = new(
        $"""(version|encoding|standalone){Ws}*={Ws}*(?:"([^"]*)"|'([^']*)')""", RegexOptions.CultureInvariant);

    // Encodings whose markup bytes are the same as UTF-8's, so this scanner reads what a renderer reads.
    static readonly Regex SameMarkupEncoding = new(
        "^(utf-8|us-ascii|iso-8859-[0-9]{1,2}|windows-125[0-8])$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Only version 1.0 or 1.1, an encoding and standalone yes or no, so nothing else can ride in it.
    static bool IsPlainXmlDeclaration(string text, int offset)
    {
        var m = XmlDeclaration.Match(text);
        if (!m.Success) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match a in PseudoAttribute.Matches(m.Groups[1].Value))
        {
            var key = a.Groups[1].Value;
            var value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value;
            if (!seen.Add(key)) return false;
            if (key == "version" && value is not ("1.0" or "1.1")) return false;
            if (key == "standalone" && value is not ("yes" or "no")) return false;
            if (key == "encoding" && !SameMarkupEncoding.IsMatch(value))
                throw new ProvStripFormatException($"SVG encoding \"{value}\" is not supported", offset);
        }
        return true;
    }

    // A bare doctype, or a W3C SVG public identifier with no system URL or that version's own URL.
    static readonly Regex Doctype = new(
        $"""^<!DOCTYPE{Ws}+svg(?:{Ws}+PUBLIC{Ws}+(["'])-//W3C//DTD SVG (1\.0|1\.1|1\.1 Basic|1\.1 Tiny)//EN\1(?:{Ws}+(["'])([^"']*)\3)?)?{Ws}*>$""",
        RegexOptions.CultureInvariant);

    static readonly Dictionary<string, string> DtdUrls = new(StringComparer.Ordinal)
    {
        ["1.0"] = "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd",
        ["1.1"] = "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd",
        ["1.1 Basic"] = "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-basic.dtd",
        ["1.1 Tiny"] = "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-tiny.dtd",
    };

    static bool IsPlainDoctype(string text)
    {
        var m = Doctype.Match(text);
        if (!m.Success) return false;
        return !m.Groups[4].Success || DtdUrls[m.Groups[2].Value] == m.Groups[4].Value;
    }

    // The doctype may hold an internal subset in brackets, with comments, processing instructions and quoted
    // strings inside it that can contain any of [ ] > ' ".
    static (int End, bool Subset) SkipDoctype(ReadOnlySpan<byte> b, int i)
    {
        var subset = false;
        var depth = 0;
        byte quote = 0;
        for (var j = i + 9; j < b.Length; j++)
        {
            var c = b[j];
            if (quote != 0) { if (c == quote) quote = 0; continue; }
            if (depth > 0 && b[j..].StartsWith("<!--"u8)) { j = After(b, j + 4, "-->"u8, "unclosed comment in doctype", j) - 1; continue; }
            if (depth > 0 && b[j..].StartsWith("<?"u8)) { j = After(b, j + 2, "?>"u8, "unclosed instruction in doctype", j) - 1; continue; }
            if (c is (byte)'"' or (byte)'\'') quote = c;
            else if (c == '[') { depth++; subset = true; }
            else if (c == ']') depth--;
            else if (c == '>' && depth <= 0) return (j + 1, subset);
        }
        throw new ProvStripFormatException("unclosed doctype", i);
    }

    static int After(ReadOnlySpan<byte> b, int from, ReadOnlySpan<byte> close, string error, int start)
    {
        var at = b[from..].IndexOf(close);
        if (at < 0) throw new ProvStripFormatException(error, start);
        return from + at + close.Length;
    }

    static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    public bool IsStructural(Segment segment, ReadOnlySpan<byte> bytes) => false;

    public bool IsSafeToDrop(Segment segment) => segment.Label != "#doctype";

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

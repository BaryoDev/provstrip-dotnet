using System.Text;

namespace BaryoDev.ProvStrip.Formats;

public static class FormatDetector
{
    static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>"png", "jpeg", "webp", "svg", or null. Looks at the bytes only, never a file name.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature)) return "png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "webp";
        return LooksLikeSvg(bytes) ? "svg" : null;
    }

    /// <summary>Skips a BOM, whitespace, an XML declaration, comments and a doctype, then needs &lt;svg.</summary>
    internal static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var head = bytes[..Math.Min(bytes.Length, 4096)];
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) head = head[3..];
        var text = Encoding.UTF8.GetString(head);
        var i = 0;
        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (Skip(text, ref i, "<?", "?>") || Skip(text, ref i, "<!--", "-->") || SkipDoctype(text, ref i))
                continue;
            break;
        }
        return text.AsSpan(i).StartsWith("<svg") && i + 4 <= text.Length
            && (i + 4 == text.Length || !char.IsLetterOrDigit(text[i + 4]));
    }

    // A doctype's internal subset is in brackets and holds '>' of its own.
    static bool SkipDoctype(string text, ref int i)
    {
        if (!text.AsSpan(i).StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) return false;
        var gt = text.IndexOf('>', i);
        var open = text.IndexOf('[', i);
        if (open >= 0 && (gt < 0 || open < gt))
        {
            var close = text.IndexOf("]", open, StringComparison.Ordinal);
            gt = close < 0 ? -1 : text.IndexOf('>', close);
        }
        i = gt < 0 ? text.Length : gt + 1;
        return true;
    }

    static bool Skip(string text, ref int i, string open, string close)
    {
        if (!text.AsSpan(i).StartsWith(open, StringComparison.OrdinalIgnoreCase)) return false;
        var end = text.IndexOf(close, i + open.Length, StringComparison.Ordinal);
        i = end < 0 ? text.Length : end + close.Length;
        return true;
    }
}

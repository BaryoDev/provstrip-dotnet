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

    /// <summary>
    /// Skips a BOM, whitespace, an XML declaration, comments and a doctype, then needs &lt;svg. It reads as far
    /// as it must, so a long leading comment does not hide the file.
    /// </summary>
    internal static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var i = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        while (true)
        {
            while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
            if (Skip(bytes, ref i, "<?"u8, "?>"u8) || Skip(bytes, ref i, "<!--"u8, "-->"u8) || SkipDoctype(bytes, ref i))
                continue;
            break;
        }
        var rest = bytes[i..];
        return rest.StartsWith("<svg"u8) && (rest.Length == 4 || !char.IsAsciiLetterOrDigit((char)rest[4]));
    }

    // A doctype's internal subset is in brackets and holds '>' of its own.
    static bool SkipDoctype(ReadOnlySpan<byte> bytes, ref int i)
    {
        var rest = bytes[i..];
        if (!rest.StartsWith("<!DOCTYPE"u8) && !rest.StartsWith("<!doctype"u8)) return false;
        var gt = rest.IndexOf((byte)'>');
        var open = rest.IndexOf((byte)'[');
        if (open >= 0 && (gt < 0 || open < gt))
        {
            var close = rest[open..].IndexOf((byte)']');
            gt = -1;
            if (close >= 0)
            {
                var after = rest[(open + close)..].IndexOf((byte)'>');
                if (after >= 0) gt = open + close + after;
            }
        }
        i = gt < 0 ? bytes.Length : i + gt + 1;
        return true;
    }

    static bool Skip(ReadOnlySpan<byte> bytes, ref int i, ReadOnlySpan<byte> open, ReadOnlySpan<byte> close)
    {
        if (!bytes[i..].StartsWith(open)) return false;
        var end = bytes[(i + open.Length)..].IndexOf(close);
        i = end < 0 ? bytes.Length : i + open.Length + end + close.Length;
        return true;
    }
}

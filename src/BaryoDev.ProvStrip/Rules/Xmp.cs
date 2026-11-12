using System.Text;

namespace BaryoDev.ProvStrip.Rules;

internal static class Xmp
{
    /// <summary>True when any marker appears in the payload, compared as ASCII without case.</summary>
    public static bool NamesATool(ReadOnlySpan<byte> payload, IReadOnlyList<string> markers) =>
        ContainsAny(payload, markers);

    internal static bool ContainsAny(ReadOnlySpan<byte> payload, IReadOnlyList<string> needles)
    {
        if (needles.Count == 0) return false;
        var text = Encoding.Latin1.GetString(payload);
        foreach (var n in needles)
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

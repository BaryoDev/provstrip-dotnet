using System.Text;
using System.Text.Json;

namespace BaryoDev.ProvStrip.Tests;

internal static class Fixtures
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string PathOf(string name) => Path.Combine(Dir, name);

    public static byte[] Read(string name) => File.ReadAllBytes(PathOf(name));

    /// <summary>Every image fixture on disk.</summary>
    public static IEnumerable<string> All() =>
        Directory.GetFiles(Dir).Select(Path.GetFileName)
            .Where(n => n is not null && Path.GetExtension(n) is ".png" or ".jpg" or ".webp" or ".svg")!;

    public static IReadOnlyList<string> ManifestNames() =>
        JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathOf("manifest.json")))!;

    /// <summary>Case-insensitive ASCII search for any of the needles.</summary>
    public static bool ContainsAny(string name, params string[] needles)
    {
        var text = Encoding.Latin1.GetString(Read(name));
        return needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
    }
}

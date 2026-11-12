namespace BaryoDev.ProvStrip.Tests;

internal static class Temp
{
    /// <summary>Copies a fixture into a fresh directory of its own, optionally under another name.</summary>
    public static string Copy(string fixture, string? @as = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "provstrip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, @as ?? fixture);
        File.Copy(Fixtures.PathOf(fixture), path);
        return path;
    }
}

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

    /// <summary>A fresh directory holding copies of the named fixtures.</summary>
    public static string Dir(params string[] fixtures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "provstrip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in fixtures) File.Copy(Fixtures.PathOf(f), Path.Combine(dir, f));
        return dir;
    }
}

using System.Text.Json;
using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

/// <summary>The backoffice files ship as static web assets; these read them from the source tree.</summary>
public class ClientAssetTests
{
    static readonly string Wwwroot = FindWwwroot();
    static readonly string Plugin = Path.Combine(Wwwroot, "App_Plugins", "BaryoDev.ProvStrip");

    static string FindWwwroot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BaryoDev.Umbraco.ProvStrip", "wwwroot");
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "BaryoDev.Umbraco.ProvStrip"))) return candidate;
        }
        throw new DirectoryNotFoundException("src/BaryoDev.Umbraco.ProvStrip not found above the test output");
    }

    static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Plugin, "umbraco-package.json"))).RootElement;

    static IEnumerable<JsonElement> Extensions() => Manifest().GetProperty("extensions").EnumerateArray();

    static JsonElement Condition(JsonElement extension) => extension.GetProperty("conditions").EnumerateArray().Single();

    [Fact] public void ManifestParsesAndNamesBothExtensions()
    {
        var info = Extensions().Single(e => e.GetProperty("type").GetString() == "workspaceInfoApp");
        (Condition(info).GetProperty("alias").GetString(), Condition(info).GetProperty("match").GetString())
            .ShouldBe(("Umb.Condition.WorkspaceAlias", "Umb.Workspace.Media"));
        var dashboard = Extensions().Single(e => e.GetProperty("type").GetString() == "dashboard");
        (Condition(dashboard).GetProperty("alias").GetString(), Condition(dashboard).GetProperty("match").GetString())
            .ShouldBe(("Umb.Condition.SectionAlias", "Umb.Section.Media"));
        dashboard.GetProperty("meta").GetProperty("label").GetString().ShouldBe("ProvStrip");
    }

    [Fact] public void EveryManifestFileExists()
    {
        var paths = Extensions().SelectMany(e => new[] { "element", "js", "api" }
                .Where(p => e.TryGetProperty(p, out _)).Select(p => e.GetProperty(p).GetString()!))
            .ToList();
        paths.ShouldNotBeEmpty();
        foreach (var path in paths)
        {
            path.ShouldStartWith("/App_Plugins/BaryoDev.ProvStrip/");
            File.Exists(Path.Combine(Wwwroot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))).ShouldBeTrue(path);
        }
    }

    [Fact] public void CopyHasNoDashesOrArrows()
    {
        var files = Directory.EnumerateFiles(Plugin, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".js") || f.EndsWith(".json") || f.EndsWith(".css")).ToList();
        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            text.ShouldNotContain("—", customMessage: file);
            text.ShouldNotContain("→", customMessage: file);
        }
    }

    // The package strips AI attribution from images; its own images must not carry any.
    [Fact] public void IconsAreClean()
    {
        var images = Directory.EnumerateFiles(Wwwroot, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".svg") || f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".webp")).ToList();
        images.ShouldNotBeEmpty();
        foreach (var image in images) ProvStripEngine.Inspect(File.ReadAllBytes(image)).IsClean.ShouldBeTrue(image);
    }
}

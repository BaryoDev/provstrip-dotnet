using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

/// <summary>Packs the Umbraco package once and checks what a site would install.</summary>
public class PackageTests : IClassFixture<PackageTests.Packed>
{
    readonly Packed packed;

    public PackageTests(Packed packed) => this.packed = packed;

    public sealed class Packed : IDisposable
    {
        readonly string output = Path.Combine(Path.GetTempPath(), $"provstrip-pack-{Guid.NewGuid():N}");

        public List<string> Entries { get; }
        public string Nuspec { get; }
        public Dictionary<string, byte[]> Images { get; } = [];

        public Packed()
        {
            var root = RepoRoot();
            var version = Regex.Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
            var pack = Process.Start(new ProcessStartInfo("dotnet", ["pack", Path.Combine(root, "src", "BaryoDev.Umbraco.ProvStrip"), "-c", "Release", "-o", output, "-v", "q", "-nologo"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var log = pack.StandardOutput.ReadToEndAsync();
            var errors = pack.StandardError.ReadToEndAsync();
            if (!pack.WaitForExit(TimeSpan.FromMinutes(5))) { pack.Kill(entireProcessTree: true); throw new TimeoutException("dotnet pack took over 5 minutes"); }
            if (pack.ExitCode != 0) throw new InvalidOperationException($"dotnet pack failed:\n{log.Result}\n{errors.Result}");

            using var zip = ZipFile.OpenRead(Path.Combine(output, $"BaryoDev.Umbraco.ProvStrip.{version}.nupkg"));
            Entries = zip.Entries.Select(e => e.FullName).ToList();
            using (var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".nuspec")).Open())) Nuspec = reader.ReadToEnd();
            foreach (var entry in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"\.(png|jpe?g|webp|svg)$", RegexOptions.IgnoreCase)))
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                Images[entry.FullName] = copy.ToArray();
            }
        }

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "provstrip-dotnet.slnx"))) return dir.FullName;
            throw new DirectoryNotFoundException("provstrip-dotnet.slnx not found above the test output");
        }

        public void Dispose()
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact] public void PackageHoldsTheLibraryAndTheManifest()
    {
        packed.Entries.ShouldContain("lib/net10.0/BaryoDev.Umbraco.ProvStrip.dll");
        packed.Entries.ShouldContain(e => e.EndsWith("App_Plugins/BaryoDev.ProvStrip/umbraco-package.json"));
        packed.Entries.ShouldContain(e => e.EndsWith("App_Plugins/BaryoDev.ProvStrip/provstrip-dashboard.js"));
        packed.Nuspec.ShouldContain("<id>BaryoDev.Umbraco.ProvStrip</id>");
    }

    [Fact] public void PackageDependsOnTheEngineAndAnUmbracoRange()
    {
        packed.Nuspec.ShouldContain("""id="BaryoDev.ProvStrip" """.Trim());
        packed.Nuspec.ShouldContain("""id="Umbraco.Cms.Api.Management" version="[17.8.1, 19.0.0)" """.Trim());
    }

    [Fact] public void ListingCarriesTheReadmeIconAndMarketplaceTag()
    {
        packed.Entries.ShouldContain("README.md");
        packed.Entries.ShouldContain(e => e == "icon-umbraco.png" || e == "icon.png");
        packed.Nuspec.ShouldContain("umbraco-marketplace");
    }

    // A package that strips AI attribution from images ships images without any.
    [Fact] public void EveryPackedImageIsClean()
    {
        packed.Images.ShouldNotBeEmpty();
        foreach (var (name, bytes) in packed.Images) ProvStripEngine.Inspect(bytes).IsClean.ShouldBeTrue(name);
    }
}

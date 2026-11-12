using System.Diagnostics;
using System.IO.Compression;
using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.ProvStrip.Tests;

public class PackStepTests
{
    sealed record PackRun(int ExitCode, string Output, string Dir)
    {
        public string SourceFile(string name) => Path.Combine(Dir, name);

        public byte[] PackedFile(string name)
        {
            using var zip = ZipFile.OpenRead(Path.Combine(Dir, "out", "ProvStrip.Sample.1.0.0.nupkg"));
            using var entry = zip.GetEntry(name)!.Open();
            using var copy = new MemoryStream();
            entry.CopyTo(copy);
            return copy.ToArray();
        }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "provstrip-dotnet.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // The test runs from bin/<Configuration>/net10.0, and the task was built in the same configuration.
    static string TaskAssembly()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        return Path.Combine(RepoRoot(), "src", "BaryoDev.ProvStrip.Build", "bin", configuration, "net8.0", "BaryoDev.ProvStrip.Build.dll");
    }

    static PackRun Pack(string icon, params string[] properties) => Pack(icon, skipIcon: false, properties);

    static PackRun Pack(string icon, bool skipIcon, params string[] properties)
    {
        var dir = Path.Combine(Path.GetTempPath(), "provstrip-pack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sample = Path.Combine(RepoRoot(), "tests", "BaryoDev.ProvStrip.Build.Tests", "sample");
        var csproj = File.ReadAllText(Path.Combine(sample, "Sample.csproj"));
        if (skipIcon) csproj = csproj.Replace("""Pack="true" PackagePath="/" """, """Pack="true" PackagePath="/" ProvStripSkip="true" """);
        File.WriteAllText(Path.Combine(dir, "Sample.csproj"), csproj);
        File.Copy(Path.Combine(sample, "nuget.config"), Path.Combine(dir, "nuget.config"));
        File.Copy(Fixtures.PathOf(icon), Path.Combine(dir, "icon.png"));

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["pack", "Sample.csproj", "-o", "out", "--disable-build-servers", "-nodeReuse:false",
                     $"-p:ProvStripTaskAssembly={TaskAssembly()}",
                     $"-p:ProvStripImport={Path.Combine(RepoRoot(), "src", "BaryoDev.ProvStrip.Build", "build")}{Path.DirectorySeparatorChar}",
                     .. properties])
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3))) process.Kill(entireProcessTree: true);
        return new PackRun(process.ExitCode, stdout.Result + stderr.Result, dir);
    }

    [Fact] public void C2paIconFailsThePack() =>
        Pack("c2pa.png").ShouldSatisfyAllConditions(r => r.ExitCode.ShouldNotBe(0), r => r.Output.ShouldContain("PROVSTRIP001"), r => r.Output.ShouldContain("icon.png"));

    [Fact] public void AutoStripsThePackedCopyOnly()
    {
        var r = Pack("c2pa.png", "-p:ProvStripProvenance=auto");
        r.ExitCode.ShouldBe(0, r.Output);
        ProvStripEngine.Inspect(r.PackedFile("icon.png")).IsClean.ShouldBeTrue();
        File.ReadAllBytes(r.SourceFile("icon.png")).ShouldBe(Fixtures.Read("c2pa.png"));
    }

    [Fact] public void CameraDataFailsByDefault() => Pack("camera.png").Output.ShouldContain("PROVSTRIP002");

    [Fact] public void SkipMetadataLetsItThrough() => Pack("c2pa.png", skipIcon: true).ExitCode.ShouldBe(0);

    [Fact] public void UnknownFailsEvenWithAuto() =>
        Pack("unknown-ancillary.png", "-p:ProvStripProvenance=auto", "-p:ProvStripCamera=auto").Output.ShouldContain("error PROVSTRIP003");

    [Fact] public void KeepUnknownWarns() =>
        Pack("unknown-ancillary.png", "-p:ProvStripKeepUnknown=true").ShouldSatisfyAllConditions(
            r => r.ExitCode.ShouldBe(0, r.Output), r => r.Output.ShouldContain("warning PROVSTRIP003"));

    [Fact] public void CleanIconPacks()
    {
        var r = Pack("clean.png");
        r.ExitCode.ShouldBe(0, r.Output);
        r.PackedFile("icon.png").ShouldBe(Fixtures.Read("clean.png"));
    }

    static (int ExitCode, string Output) Run(string dir, IDictionary<string, string>? env, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) start.Environment[k] = v;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5))) process.Kill(entireProcessTree: true);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    // The package as NuGet consumers get it: packed to a local feed, restored into a clean package folder.
    // A project with TargetFrameworks packs in the outer build, which imports buildMultiTargeting/, not build/.
    [Theory]
    [InlineData("TargetFramework", "net10.0")]
    [InlineData("TargetFrameworks", "net8.0;net10.0")]
    public void ConsumerOfThePackageIsChecked(string property, string frameworks)
    {
        var dir = Path.Combine(Path.GetTempPath(), "provstrip-pack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var packed = Run(dir, null, "pack", Path.Combine(RepoRoot(), "src", "BaryoDev.ProvStrip.Build", "BaryoDev.ProvStrip.Build.csproj"),
            "-c", configuration, "--no-build", "-o", Path.Combine(dir, "feed"), "-p:Version=0.0.0-consumer", "--disable-build-servers", "-nodeReuse:false");
        packed.ExitCode.ShouldBe(0, packed.Output);

        File.WriteAllText(Path.Combine(dir, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(dir, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <{property}>{frameworks}</{property}>
                <PackageId>ProvStrip.Consumer</PackageId>
                <Version>1.0.0</Version>
                <Authors>sample</Authors>
                <Description>A consumer of the ProvStrip pack step.</Description>
                <PackageIcon>icon.png</PackageIcon>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="BaryoDev.ProvStrip.Build" Version="0.0.0-consumer" />
                <None Include="icon.png" Pack="true" PackagePath="/" />
              </ItemGroup>
            </Project>
            """);
        File.Copy(Fixtures.PathOf("c2pa.png"), Path.Combine(dir, "icon.png"));

        var env = new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(dir, "packages") };
        var r = Run(dir, env, "pack", "Consumer.csproj", "-o", "out", "--disable-build-servers", "-nodeReuse:false");
        r.ExitCode.ShouldNotBe(0, r.Output);
        r.Output.ShouldContain("PROVSTRIP001");
    }

    // Packs a project written into a fresh folder, with fixtures copied to the given paths, through the pack step from source.
    static (int ExitCode, string Output, string Dir) PackProject(string csproj, (string Path, string Fixture)[] files, params string[] properties)
    {
        var dir = Path.Combine(Path.GetTempPath(), "provstrip-pack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Project.csproj"), csproj);
        File.Copy(Path.Combine(RepoRoot(), "tests", "BaryoDev.ProvStrip.Build.Tests", "sample", "nuget.config"), Path.Combine(dir, "nuget.config"));
        foreach (var (path, fixture) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
            File.Copy(Fixtures.PathOf(fixture), Path.Combine(dir, path));
        }
        var r = Run(dir, null, ["pack", "Project.csproj", "-o", "out", "--disable-build-servers", "-nodeReuse:false",
            $"-p:ProvStripTaskAssembly={TaskAssembly()}",
            $"-p:ProvStripImport={Path.Combine(RepoRoot(), "src", "BaryoDev.ProvStrip.Build", "build")}{Path.DirectorySeparatorChar}",
            .. properties]);
        return (r.ExitCode, r.Output, dir);
    }

    static string Project(string sdk, string items) => $"""
        <Project Sdk="{sdk}">
          <Import Project="$(ProvStripImport)BaryoDev.ProvStrip.Build.props" />
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>ProvStrip.Project</PackageId>
            <Version>1.0.0</Version>
            <Authors>sample</Authors>
            <Description>A sample package for the ProvStrip pack step tests.</Description>
          </PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
          <Import Project="$(ProvStripImport)BaryoDev.ProvStrip.Build.targets" />
        </Project>
        """;

    static SortedSet<string> Entries(string dir)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(dir, "out", "ProvStrip.Project.1.0.0.nupkg"));
        return new(zip.Entries.Select(e => e.FullName).Where(n => !n.StartsWith("package/services/", StringComparison.Ordinal)), StringComparer.Ordinal);
    }

    // With no PackagePath, NuGet works the path out from where the item sits, so a cleaned copy under obj/ would
    // land somewhere else in the package. The pack either keeps the original path or refuses.
    [Fact]
    public void ItemWithoutPackagePathIsNotMovedInAutoMode()
    {
        var csproj = Project("Microsoft.NET.Sdk", """<None Include="assets/logo.png" Pack="true" />""");
        var plain = PackProject(csproj, [("assets/logo.png", "c2pa.png")], "-p:ProvStripEnabled=false");
        plain.ExitCode.ShouldBe(0, plain.Output);
        var auto = PackProject(csproj, [("assets/logo.png", "c2pa.png")], "-p:ProvStripProvenance=auto");
        if (auto.ExitCode != 0)
        {
            auto.Output.ShouldContain("PROVSTRIP006");
            return;
        }
        Entries(auto.Dir).ShouldBe(Entries(plain.Dir));
    }

    [Fact]
    public void CleanItemWithoutPackagePathPacks()
    {
        var r = PackProject(Project("Microsoft.NET.Sdk", """<None Include="assets/logo.png" Pack="true" />"""), [("assets/logo.png", "clean.png")]);
        r.ExitCode.ShouldBe(0, r.Output);
        Entries(r.Dir).ShouldContain("content/assets/logo.png");
    }

    // A Razor class library records each static web asset's length and hash in the package. A packed copy
    // that differs from those would be served with the wrong Content-Length and integrity.
    [Fact]
    public void StaticWebAssetIsNotChangedBehindItsManifest()
    {
        var r = PackProject(Project("Microsoft.NET.Sdk.Razor", ""), [("wwwroot/img.png", "c2pa.png")], "-p:ProvStripProvenance=auto");
        if (r.ExitCode != 0)
        {
            r.Output.ShouldContain("PROVSTRIP005");
            return;
        }
        using var zip = ZipFile.OpenRead(Path.Combine(r.Dir, "out", "ProvStrip.Project.1.0.0.nupkg"));
        using var asset = new MemoryStream();
        using (var e = zip.GetEntry("staticwebassets/img.png")!.Open()) e.CopyTo(asset);
        using var reader = new StreamReader(zip.GetEntry("build/Microsoft.AspNetCore.StaticWebAssets.props")!.Open());
        var props = reader.ReadToEnd();
        props.ShouldContain($"<Integrity>{Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(asset.ToArray()))}</Integrity>");
        props.ShouldContain($"<FileLength>{asset.Length}</FileLength>");
    }

    [Fact] public void BadModeFailsThePack() => Pack("clean.png", "-p:ProvStripCamera=sometimes").Output.ShouldContain("PROVSTRIP004");

    [Fact] public void DisabledSkipsTheCheck() => Pack("c2pa.png", "-p:ProvStripEnabled=false").ExitCode.ShouldBe(0);
}

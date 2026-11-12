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

    [Fact] public void BadModeFailsThePack() => Pack("clean.png", "-p:ProvStripCamera=sometimes").Output.ShouldContain("PROVSTRIP004");

    [Fact] public void DisabledSkipsTheCheck() => Pack("c2pa.png", "-p:ProvStripEnabled=false").ExitCode.ShouldBe(0);
}

using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.ProvStrip.Tests;

public class FileStripperTests
{
    static readonly StripPolicy AutoProvenance = new(CategoryMode.Auto);

    [Fact] public void InPlaceLeavesNoTempFile()
    {
        var p = Temp.Copy("c2pa.png");
        FileStripper.Run(p, AutoProvenance);
        Directory.GetFiles(Path.GetDirectoryName(p)!, "*.provstrip.tmp").ShouldBeEmpty();
    }

    [Fact] public void InPlaceStripsTheFile()
    {
        var p = Temp.Copy("c2pa.png");
        FileStripper.Run(p, AutoProvenance).Written.ShouldBeTrue();
        ProvStripEngine.Inspect(File.ReadAllBytes(p)).IsClean.ShouldBeTrue();
    }

    [Fact] public void InPlaceOnALinkStripsTheTargetAndKeepsTheLink()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Temp.Copy("c2pa.png");
        var link = Path.Combine(Path.GetDirectoryName(target)!, "link.png");
        File.CreateSymbolicLink(link, target);

        FileStripper.Run(link, AutoProvenance).Written.ShouldBeTrue();

        new FileInfo(link).LinkTarget.ShouldBe(target);
        ProvStripEngine.Inspect(File.ReadAllBytes(target)).IsClean.ShouldBeTrue();
        Directory.GetFiles(Path.GetDirectoryName(target)!, "*.provstrip.tmp").ShouldBeEmpty();
    }

    [Fact] public void BlockedWritesNothing()
    {
        var p = Temp.Copy("unknown-ancillary.png");
        var before = File.ReadAllBytes(p);
        Should.Throw<UnknownSegmentException>(() => FileStripper.Run(p, new StripPolicy(CategoryMode.Auto, CategoryMode.Auto)));
        File.ReadAllBytes(p).ShouldBe(before);
    }

    [Fact] public void FlagModeWritesNothing()
    {
        var p = Temp.Copy("camera.jpg");
        var before = File.ReadAllBytes(p);
        FileStripper.Run(p, new StripPolicy()).Written.ShouldBeFalse();
        File.ReadAllBytes(p).ShouldBe(before);
    }

    [Fact] public void CorruptFileWritesNothing()
    {
        var p = Temp.Copy("bad-crc.png");
        var before = File.ReadAllBytes(p);
        Should.Throw<ProvStripFormatException>(() => FileStripper.Run(p, AutoProvenance));
        File.ReadAllBytes(p).ShouldBe(before);
    }

    [Fact] public void OutputPathLeavesTheInputAlone()
    {
        var p = Temp.Copy("c2pa.png");
        var before = File.ReadAllBytes(p);
        var output = Path.Combine(Path.GetDirectoryName(p)!, "out.png");
        FileStripper.Run(p, AutoProvenance, output).Written.ShouldBeTrue();
        File.ReadAllBytes(p).ShouldBe(before);
        ProvStripEngine.Inspect(File.ReadAllBytes(output)).IsClean.ShouldBeTrue();
    }

    [Fact] public void OutputPathGetsACopyWhenNothingChanged()
    {
        var p = Temp.Copy("clean.png");
        var output = Path.Combine(Path.GetDirectoryName(p)!, "out.png");
        FileStripper.Run(p, AutoProvenance, output).Written.ShouldBeTrue();
        File.ReadAllBytes(output).ShouldBe(File.ReadAllBytes(p));
    }

    [Fact] public void InPlaceKeepsTheFileMode()
    {
        if (OperatingSystem.IsWindows()) return;
        var p = Temp.Copy("c2pa.png");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(p, mode);
        FileStripper.Run(p, AutoProvenance);
        File.GetUnixFileMode(p).ShouldBe(mode);
    }
}

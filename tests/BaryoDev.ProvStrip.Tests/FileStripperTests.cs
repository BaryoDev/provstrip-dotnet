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

    [Fact] public void InPlaceOnALinkIsRefusedAndNothingChanges()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Temp.Copy("c2pa.png");
        var before = File.ReadAllBytes(target);
        var link = Path.Combine(Path.GetDirectoryName(target)!, "link.png");
        File.CreateSymbolicLink(link, target);

        Should.Throw<IOException>(() => FileStripper.Run(link, AutoProvenance)).Message.ShouldContain("symbolic link");

        new FileInfo(link).LinkTarget.ShouldBe(target);
        File.ReadAllBytes(target).ShouldBe(before);
    }

    [Fact] public void InPlaceOnALinkOutsideTheFolderLeavesTheTargetAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Temp.Copy("c2pa.png");
        var before = File.ReadAllBytes(outside);
        var root = Temp.Dir();
        File.CreateSymbolicLink(Path.Combine(root, "icon.png"), outside);

        Should.Throw<IOException>(() => FileStripper.Run(Path.Combine(root, "icon.png"), AutoProvenance));

        File.ReadAllBytes(outside).ShouldBe(before);
    }

    [Fact] public void OutputPathMayReadThroughALink()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Temp.Copy("c2pa.png");
        var link = Path.Combine(Path.GetDirectoryName(target)!, "link.png");
        File.CreateSymbolicLink(link, target);
        var output = Path.Combine(Path.GetDirectoryName(target)!, "out.png");

        FileStripper.Run(link, AutoProvenance, output).Written.ShouldBeTrue();

        ProvStripEngine.Inspect(File.ReadAllBytes(output)).IsClean.ShouldBeTrue();
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

using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Engine;

/// <summary>What happened to one file.</summary>
public sealed record FileOutcome(string Path, string Format, Decision Decision, StripResult? Result, bool Written);

public static class FileStripper
{
    /// <summary>
    /// Inspects a file, applies the policy and writes the result. In place (no output path) the write goes to a
    /// temp file in the same directory and is moved over the original, so a reader never sees half a file.
    /// Nothing is written when the policy is blocked, the file is broken, or nothing changed in place.
    /// </summary>
    public static FileOutcome Run(string inputPath, StripPolicy policy, string? outputPath = null, RuleSet? rules = null)
    {
        var before = new FileInfo(inputPath);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var input = File.ReadAllBytes(inputPath);
        var inspection = ProvStripEngine.Inspect(input, rules);
        var decision = policy.Decide(inspection);
        if (decision.Blocked) throw new UnknownSegmentException(decision.Unknown);

        var result = decision.Remove.Count > 0 ? ProvStripEngine.Strip(input, decision.Remove, rules) : null;
        if (outputPath is not null)
        {
            WriteAtomically(outputPath, result?.Output ?? input, inputPath);
            return new FileOutcome(inputPath, inspection.Format, decision, result, Written: true);
        }
        if (result is not { Changed: true })
            return new FileOutcome(inputPath, inspection.Format, decision, result, Written: false);

        // The file may have been edited since it was read. Stripping the old bytes over it would lose that edit.
        WriteAtomically(inputPath, result.Output, inputPath, () =>
        {
            var now = new FileInfo(Path.GetFullPath(inputPath));
            if (now.Length != length || now.LastWriteTimeUtc != modified)
                throw new IOException($"{inputPath} changed while it was being stripped, so nothing was written. Run it again.");
        });
        return new FileOutcome(inputPath, inspection.Format, decision, result, Written: true);
    }

    static void WriteAtomically(string target, byte[] bytes, string modeFrom, Action? beforeMove = null)
    {
        var full = Path.GetFullPath(target);
        // Writing through a link: the target is replaced, not the link.
        if (beforeMove is not null && File.ResolveLinkTarget(full, returnFinalTarget: true) is { } linked) full = linked.FullName;
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.provstrip.tmp");
        var created = false;
        try
        {
            // A leftover temp (or a link planted in its place) is removed, then the temp is created fresh.
            File.Delete(temp);
            // Owner-only from the first byte; the original's mode is applied once the content is complete.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                created = true;
                stream.Write(bytes);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(modeFrom));
            beforeMove?.Invoke();
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            if (created) File.Delete(temp);
            throw;
        }
    }
}

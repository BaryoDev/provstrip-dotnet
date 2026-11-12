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

        WriteAtomically(inputPath, result.Output, inputPath);
        return new FileOutcome(inputPath, inspection.Format, decision, result, Written: true);
    }

    static void WriteAtomically(string target, byte[] bytes, string modeFrom)
    {
        var full = Path.GetFullPath(target);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.provstrip.tmp");
        var created = false;
        try
        {
            // A leftover temp (or a link planted in its place) is removed, then the temp is created fresh.
            File.Delete(temp);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                stream.Write(bytes);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(modeFrom));
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            if (created) File.Delete(temp);
            throw;
        }
    }
}

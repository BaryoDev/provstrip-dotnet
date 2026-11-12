using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Formats;
using BaryoDev.ProvStrip.Rules;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace BaryoDev.ProvStrip.Build;

/// <summary>
/// Checks every image going into a package. Flag mode fails the pack; auto mode writes a cleaned copy under
/// OutputDirectory and swaps it into the package. Source files are never written.
/// </summary>
public sealed class ProvStripPackTask : Microsoft.Build.Utilities.Task
{
    [Required] public ITaskItem[] Files { get; set; } = [];
    public string Provenance { get; set; } = "flag";
    public string Camera { get; set; } = "flag";
    public bool KeepUnknown { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string? RulesFile { get; set; }
    [Output] public ITaskItem[] Originals { get; set; } = [];
    [Output] public ITaskItem[] Cleaned { get; set; } = [];

    public override bool Execute()
    {
        if (Mode(Provenance, "ProvStripProvenance") is not { } provenance || Mode(Camera, "ProvStripCamera") is not { } camera)
            return false;

        RuleSet? rules = null;
        if (!string.IsNullOrEmpty(RulesFile))
        {
            try
            {
                rules = RuleSet.Load(RulesFile);
            }
            catch (Exception e) when (e is ProvStripException or IOException or UnauthorizedAccessException)
            {
                Log.LogError(null, "PROVSTRIP004", null, RulesFile, 0, 0, 0, 0, $"ProvStripRulesFile could not be read: {e.Message}");
                return false;
            }
        }

        var policy = new StripPolicy(provenance, camera, KeepUnknown);
        var originals = new List<ITaskItem>();
        var cleaned = new List<ITaskItem>();
        for (var index = 0; index < Files.Length; index++)
        {
            var item = Files[index];
            var path = item.GetMetadata("FullPath");
            if (!File.Exists(path) || !IsImage(path)) continue;

            var name = item.ItemSpec;
            var packagePath = item.GetMetadata("PackagePath");
            // A Razor class library records each static web asset's length and hash in the package, so its packed
            // copy must stay byte for byte the source.
            var staticWebAsset = packagePath.Replace('\\', '/').TrimStart('/').StartsWith("staticwebassets/", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(item.GetMetadata("ProvStripSkip"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogMessage(MessageImportance.Normal, $"ProvStrip: {name} skipped (ProvStripSkip)");
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            Inspection inspection;
            try
            {
                inspection = ProvStripEngine.Inspect(bytes, rules);
            }
            catch (ProvStripException e)
            {
                Log.LogError(null, "PROVSTRIP004", null, path, 0, 0, 0, 0, $"{name} could not be read: {e.Message}");
                continue;
            }

            var decision = policy.Decide(inspection);
            foreach (var group in decision.Flagged.GroupBy(f => f.Category))
            {
                var (code, label, property) = group.Key == Category.Provenance
                    ? ("PROVSTRIP001", "AI attribution", "ProvStripProvenance")
                    : ("PROVSTRIP002", "Camera data", "ProvStripCamera");
                var hint = staticWebAsset
                    ? "It is a static web asset, so strip the source file: provstrip strip -i <file>."
                    : $"Set {property}=auto to strip the packed copy, or ProvStripSkip=\"true\" on the item.";
                Log.LogError(null, code, null, path, 0, 0, 0, 0, $"{name}: {label} found ({Describe(group)}). {hint}");
            }
            foreach (var f in decision.Unknown)
            {
                var message = $"{name}: unknown {f.Segment.Label} segment at byte {f.Segment.Start}. " +
                              "Set ProvStripKeepUnknown=true to keep it, or ProvStripSkip=\"true\" on the item.";
                if (KeepUnknown) Log.LogWarning(null, "PROVSTRIP003", null, path, 0, 0, 0, 0, message);
                else Log.LogError(null, "PROVSTRIP003", null, path, 0, 0, 0, 0, message);
            }

            if (decision.Blocked || decision.Remove.Count == 0)
            {
                if (inspection.IsClean) Log.LogMessage(MessageImportance.Normal, $"ProvStrip: {name} is clean");
                continue;
            }

            if (staticWebAsset)
            {
                Log.LogError(null, "PROVSTRIP005", null, path, 0, 0, 0, 0,
                    $"{name}: {Describe(decision.Remove)} found in a static web asset. The package records its length and hash, " +
                    "so the packed copy cannot be stripped. Strip the source file: provstrip strip -i <file>.");
                continue;
            }

            // With no PackagePath, NuGet places a file by where it sits in the project, so a cleaned copy under obj/
            // would land at a different path in the package.
            if (string.IsNullOrEmpty(packagePath))
            {
                Log.LogError(null, "PROVSTRIP006", null, path, 0, 0, 0, 0,
                    $"{name}: {Describe(decision.Remove)} found, and the item has no PackagePath, so a cleaned copy cannot " +
                    "take its place. Set PackagePath on the item, or strip the source file: provstrip strip -i <file>.");
                continue;
            }

            var result = ProvStripEngine.Strip(bytes, decision.Remove, rules);
            // One folder per item keeps the file name (NuGet uses it when PackagePath is a folder) and stays
            // inside OutputDirectory whatever PackagePath says.
            var folder = Path.Combine(OutputDirectory, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(path));
            File.WriteAllBytes(copy, result.Output);

            var cleanedItem = new TaskItem(copy);
            item.CopyMetadataTo(cleanedItem);
            originals.Add(item);
            cleaned.Add(cleanedItem);
            Log.LogMessage(MessageImportance.High, $"ProvStrip: {name}: stripped {Describe(result.Removed)} from the packed copy");
        }

        Originals = [.. originals];
        Cleaned = [.. cleaned];
        return !Log.HasLoggedErrors;
    }

    CategoryMode? Mode(string value, string property)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "auto": return CategoryMode.Auto;
            case "flag": return CategoryMode.Flag;
            default:
                Log.LogError(null, "PROVSTRIP004", null, null, 0, 0, 0, 0, $"{property} must be auto or flag, not \"{value}\".");
                return null;
        }
    }

    static string Describe(IEnumerable<Finding> findings) => string.Join(", ", findings.Select(f => f.Description).Distinct());

    // Only the first 4 KB of a file is read to decide whether it is an image at all.
    static bool IsImage(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[4096];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return FormatDetector.Detect(head.AsSpan(0, read)) is not null;
    }
}

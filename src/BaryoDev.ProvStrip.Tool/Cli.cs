using System.Reflection;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Formats;
using BaryoDev.ProvStrip.Rules;
using Microsoft.Extensions.FileSystemGlobbing;

namespace BaryoDev.ProvStrip.Tool;

/// <summary>The provstrip command line, hand-parsed.</summary>
public static class Cli
{
    const int Usage = 64;

    const string Help = """
        provstrip strip <input> [<output>]
        provstrip strip -i <file|dir|glob>...
        provstrip verify <file|dir|glob>...

        options: --provenance auto|flag   (default auto for strip)
                 --camera auto|flag       (default auto for strip)
                 --keep-unknown           keep unknown segments and report them
                 --rules <file>           use a rules file instead of the built-in rules
                 --json                   machine-readable output
                 --version, --help

        exit codes: 0 done, 1 flagged findings left, 2 found something, blocked or broken, 64 usage error
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            stdout.WriteLine(Help);
            return args.Length == 0 ? Usage : 0;
        }
        if (args.Contains("--version"))
        {
            stdout.WriteLine(Version());
            return 0;
        }

        var command = args[0];
        if (command is not ("strip" or "verify")) return Fail(stderr, $"unknown command \"{command}\"");

        var provenance = CategoryMode.Auto;
        var camera = CategoryMode.Auto;
        bool keepUnknown = false, json = false, inPlace = false;
        string? rulesPath = null;
        var inputs = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--provenance" or "--camera":
                    if (i + 1 >= args.Length || ParseMode(args[i + 1]) is not { } mode)
                        return Fail(stderr, $"{a} takes auto or flag");
                    if (a == "--provenance") provenance = mode; else camera = mode;
                    i++;
                    break;
                case "--rules":
                    if (i + 1 >= args.Length) return Fail(stderr, "--rules takes a file");
                    rulesPath = args[++i];
                    break;
                case "--keep-unknown": keepUnknown = true; break;
                case "--json": json = true; break;
                case "-i" when command == "strip": inPlace = true; break;
                default:
                    if (a.StartsWith('-')) return Fail(stderr, $"unknown option {a}");
                    inputs.Add(a);
                    break;
            }
        }

        if (inputs.Count == 0) return Fail(stderr, "no input given");
        string? output = null;
        if (command == "strip" && !inPlace)
        {
            if (inputs.Count > 2) return Fail(stderr, "more than one input needs -i");
            output = inputs.Count == 2 ? inputs[1] : null;
            inputs.RemoveRange(1, inputs.Count - 1);
        }

        RuleSet? rules = null;
        if (rulesPath is not null)
        {
            try
            {
                rules = RuleSet.Load(rulesPath);
            }
            catch (Exception e) when (e is ProvStripException or IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"provstrip: {rulesPath}: {e.Message}");
                return 2;
            }
        }

        var reports = new List<FileReport>();
        foreach (var input in inputs)
        {
            var files = Expand(input, out var error);
            if (error is not null) reports.Add(new FileReport(input) { Error = error });
            foreach (var file in files)
                reports.Add(command == "verify"
                    ? Verify(file, rules)
                    : Strip(file, output, new StripPolicy(provenance, camera, keepUnknown), rules));
        }

        var verify = command == "verify";
        if (json) stdout.WriteLine(Report.Json(reports));
        else foreach (var r in reports) stdout.WriteLine(Report.Line(r, verify));
        return reports.Count == 0 ? 0 : reports.Max(r => r.Code(verify));
    }

    static FileReport Verify(string path, RuleSet? rules) => Guard(path, () =>
    {
        var inspection = ProvStripEngine.Inspect(File.ReadAllBytes(path), rules);
        return new FileReport(path)
        {
            Format = inspection.Format,
            Flagged = inspection.Findings.Where(f => f.Category != Category.Unknown).ToList(),
            Unknown = inspection.Findings.Where(f => f.Category == Category.Unknown).ToList(),
        };
    });

    static FileReport Strip(string path, string? output, StripPolicy policy, RuleSet? rules) => Guard(path, () =>
    {
        try
        {
            var outcome = FileStripper.Run(path, policy, output, rules);
            return new FileReport(path)
            {
                Format = outcome.Format,
                Removed = outcome.Result?.Removed ?? [],
                Flagged = outcome.Decision.Flagged,
                Unknown = outcome.Decision.Unknown,
            };
        }
        catch (UnknownSegmentException e)
        {
            return new FileReport(path) { Unknown = e.Unknown, UnknownBlocked = true };
        }
    });

    static FileReport Guard(string path, Func<FileReport> work)
    {
        try
        {
            return work();
        }
        catch (Exception e) when (e is ProvStripException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FileReport(path) { Error = e.Message };
        }
    }

    // A file is taken as given; a directory means every supported file below it; anything else is a glob.
    static IReadOnlyList<string> Expand(string input, out string? error)
    {
        error = null;
        if (File.Exists(input)) return [input];
        if (Directory.Exists(input))
            return Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Where(IsSupported).Order(StringComparer.Ordinal).ToList();
        if (input.IndexOfAny(['*', '?', '[']) < 0)
        {
            error = "no such file or directory";
            return [];
        }

        var full = Path.GetFullPath(input);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fixedCount = parts.TakeWhile(p => p.IndexOfAny(['*', '?', '[']) < 0).Count();
        var baseDir = Path.Combine([root, .. parts[..fixedCount]]);
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(string.Join('/', parts[fixedCount..]));
        var files = Directory.Exists(baseDir)
            ? matcher.GetResultsInFullPath(baseDir).Where(IsSupported).Order(StringComparer.Ordinal).ToList()
            : [];
        if (files.Count == 0) error = "no supported files match";
        return files;
    }

    static bool IsSupported(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4096];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return FormatDetector.Detect(head.AsSpan(0, read)) is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static CategoryMode? ParseMode(string value) => value switch
    {
        "auto" => CategoryMode.Auto,
        "flag" => CategoryMode.Flag,
        _ => null,
    };

    static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"provstrip: {message}. Run provstrip --help for usage.");
        return Usage;
    }

    static string Version()
    {
        var v = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }
}

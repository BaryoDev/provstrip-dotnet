using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaryoDev.ProvStrip.Rules;

/// <summary>
/// Classifies segments. Rules are data: a new marker in a format already parsed is an edit to the
/// rules file, not a code change. The first rule that matches wins.
/// </summary>
public sealed class RuleSet
{
    static readonly Lazy<RuleSet> DefaultSet = new(() =>
    {
        using var stream = typeof(RuleSet).Assembly.GetManifestResourceStream("BaryoDev.ProvStrip.rules.json")
            ?? throw new InvalidOperationException("embedded rules.json is missing");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    readonly IReadOnlyList<Rule> rules;

    internal IReadOnlyList<string> ProvenanceMarkers { get; }

    internal IReadOnlyList<string> StandardIccDescriptions { get; }

    RuleSet(IReadOnlyList<Rule> rules, IReadOnlyList<string> markers, IReadOnlyList<string> icc)
    {
        this.rules = rules;
        ProvenanceMarkers = markers;
        StandardIccDescriptions = icc;
    }

    /// <summary>The rules built into the package.</summary>
    public static RuleSet Default => DefaultSet.Value;

    public static RuleSet Load(string path) => Parse(File.ReadAllText(path));

    public static RuleSet Parse(string json)
    {
        RulesFile file;
        try
        {
            file = JsonSerializer.Deserialize<RulesFile>(json, JsonOptions) ?? throw new ProvStripException("rules file is empty");
        }
        catch (JsonException e)
        {
            throw new ProvStripException($"rules file is not valid JSON: {e.Message}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<Rule>();
        foreach (var r in file.Rules ?? [])
        {
            if (string.IsNullOrEmpty(r.Id)) throw new ProvStripException("a rule has no id");
            if (!seen.Add(r.Id)) throw new ProvStripException($"rule id \"{r.Id}\" is used twice");
            if (string.IsNullOrEmpty(r.Format) || string.IsNullOrEmpty(r.Label))
                throw new ProvStripException($"rule \"{r.Id}\" needs a format and a label");

            Category? category = r.Category switch
            {
                null => null,
                "provenance" => Category.Provenance,
                "camera" => Category.Camera,
                _ => throw new ProvStripException($"rule \"{r.Id}\" has unknown category \"{r.Category}\""),
            };
            Classifier? classify = r.Classify switch
            {
                null => null,
                "xmp" => Classifier.Xmp,
                "exif" => Classifier.Exif,
                "icc" => Classifier.Icc,
                _ => throw new ProvStripException($"rule \"{r.Id}\" has unknown classify \"{r.Classify}\""),
            };
            if (category is null == classify is null)
                throw new ProvStripException($"rule \"{r.Id}\" needs exactly one of category or classify");
            if (category is not null && string.IsNullOrEmpty(r.Describe))
                throw new ProvStripException($"rule \"{r.Id}\" needs a describe text");

            parsed.Add(new Rule(r.Id, r.Format, r.Label.Split('|'), r.Keyword, r.Contains ?? [], category, r.Describe, classify));
        }

        return new RuleSet(parsed, file.ProvenanceMarkers ?? [], file.StandardIccDescriptions ?? []);
    }

    internal RuleOutcome Classify(string format, Segment segment, ReadOnlySpan<byte> payload)
    {
        foreach (var rule in rules)
        {
            if (!string.Equals(rule.Format, format, StringComparison.Ordinal)) continue;
            if (!rule.Labels.Any(l => Glob(l, segment.Label))) continue;
            if (rule.Keyword is not null && (segment.Keyword is null || !Glob(rule.Keyword, segment.Keyword))) continue;
            if (rule.Contains.Count > 0 && !Xmp.ContainsAny(payload, rule.Contains)) continue;

            if (rule.Category is { } category) return new RuleOutcome.Match(category, rule.Id, rule.Describe!);
            return rule.Classify switch
            {
                Classifier.Xmp => Xmp.NamesATool(payload, ProvenanceMarkers)
                    ? new RuleOutcome.Match(Category.Provenance, rule.Id, "XMP naming a tool")
                    : new RuleOutcome.Match(Category.Camera, rule.Id, "XMP"),
                Classifier.Exif => ClassifyExif(rule, payload),
                _ => ClassifyIcc(rule, payload),
            };
        }
        return RuleOutcome.NoRule;
    }

    /// <summary>Whether a finding came from a rule whose segment is rewritten rather than dropped.</summary>
    internal bool IsExifRule(string ruleId) =>
        rules.Any(r => r.Classify == Classifier.Exif && string.Equals(r.Id, ruleId, StringComparison.Ordinal));

    static RuleOutcome ClassifyExif(Rule rule, ReadOnlySpan<byte> payload)
    {
        var summary = Exif.Read(payload);
        // Kept only when it is exactly what a strip would write, so no bytes can ride along unread.
        return summary.OnlyOrientation && Exif.OrientationOnly(payload) is { } canonical && payload.SequenceEqual(canonical)
            ? RuleOutcome.Keep
            : new RuleOutcome.Match(Category.Camera, rule.Id, Exif.Describe(summary));
    }

    // A standard profile is kept. Anything else may name the machine it came from.
    RuleOutcome ClassifyIcc(Rule rule, ReadOnlySpan<byte> payload)
    {
        var profile = IccProfile.FromPayload(rule.Format, payload);
        var description = profile is null ? null : IccProfile.Description(profile);
        if (description is not null && StandardIccDescriptions.Contains(description, StringComparer.OrdinalIgnoreCase))
            return RuleOutcome.Keep;
        return new RuleOutcome.Match(Category.Camera, rule.Id,
            description is null ? "colour profile" : $"colour profile \"{description}\"");
    }

    static bool Glob(string pattern, string value) =>
        pattern.EndsWith('*')
            ? value.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(pattern, value, StringComparison.Ordinal);

    internal enum Classifier { Xmp, Exif, Icc }

    sealed record Rule(string Id, string Format, string[] Labels, string? Keyword, IReadOnlyList<string> Contains,
        Category? Category, string? Describe, Classifier? Classify);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class RulesFile
    {
        public List<string>? ProvenanceMarkers { get; set; }
        public List<string>? StandardIccDescriptions { get; set; }
        public List<RuleDto>? Rules { get; set; }
    }

    sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Format { get; set; }
        public string? Label { get; set; }
        public string? Keyword { get; set; }
        public List<string>? Contains { get; set; }
        public string? Category { get; set; }
        public string? Classify { get; set; }
        public string? Describe { get; set; }
    }
}

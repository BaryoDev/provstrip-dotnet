using BaryoDev.ProvStrip.Formats;
using BaryoDev.ProvStrip.Rules;

namespace BaryoDev.ProvStrip.Engine;

/// <summary>What a strip produced and what it left.</summary>
public sealed record StripResult(byte[] Output, IReadOnlyList<Finding> Removed, IReadOnlyList<Finding> Remaining)
{
    public bool Changed => Removed.Count > 0;
}

public static class ProvStripEngine
{
    // Later formats register here.
    static readonly IContainer[] Containers = [new PngContainer()];

    public static Inspection Inspect(ReadOnlyMemory<byte> file, RuleSet? rules = null)
    {
        rules ??= RuleSet.Default;
        var container = Resolve(file.Span);
        var findings = new List<Finding>();
        foreach (var segment in container.ReadSegments(file))
        {
            if (container.IsStructural(segment, file.Span)) continue;
            var outcome = rules.Classify(container.Format, segment, container.Payload(segment, file).Span);
            switch (outcome)
            {
                case RuleOutcome.Match m:
                    findings.Add(new Finding(container.Format, segment, m.Category, m.RuleId, m.Description, CanDelete: true));
                    break;
                case var o when ReferenceEquals(o, RuleOutcome.NoRule):
                    findings.Add(new Finding(container.Format, segment, Category.Unknown, "unknown",
                        $"unknown {segment.Label} segment", container.IsSafeToDrop(segment)));
                    break;
            }
        }
        return new Inspection(container.Format, findings);
    }

    /// <summary>
    /// Removes the given findings, which must come from an inspection of these bytes. EXIF is rewritten to keep
    /// only its orientation; everything else is dropped. The output is inspected again before it is returned.
    /// </summary>
    public static StripResult Strip(ReadOnlyMemory<byte> file, IReadOnlyCollection<Finding> remove, RuleSet? rules = null)
    {
        rules ??= RuleSet.Default;
        var before = Inspect(file, rules);
        if (remove.Count == 0) return new StripResult(file.ToArray(), [], before.Findings);

        var removed = remove.Distinct().ToList();
        foreach (var f in removed)
        {
            if (!before.Findings.Contains(f))
                throw new ArgumentException($"{f.Description} at byte {f.Segment.Start} is not in this file", nameof(remove));
            if (!f.CanDelete)
                throw new ArgumentException($"{f.Description} at byte {f.Segment.Start} cannot be removed safely", nameof(remove));
        }

        var container = Resolve(file.Span);
        var edits = removed.Select(f => new SegmentEdit(f.Segment,
            rules.IsExifRule(f.RuleId) ? Exif.OrientationOnly(container.Payload(f.Segment, file).Span) : null)).ToList();
        var output = container.Rewrite(file, edits);

        Inspection after;
        try
        {
            after = Inspect(output, rules);
        }
        catch (ProvStripException e)
        {
            throw new ProvStripException($"verify after strip failed: output does not parse ({e.Message})");
        }

        var expected = Keys(before.Findings.Except(removed));
        var actual = Keys(after.Findings);
        if (!expected.SequenceEqual(actual))
            throw new ProvStripException(
                $"verify after strip failed: expected [{string.Join(", ", expected)}], found [{string.Join(", ", actual)}]");

        return new StripResult(output, removed, after.Findings);
    }

    static List<string> Keys(IEnumerable<Finding> findings) =>
        findings.Select(f => $"{f.RuleId}/{f.Category}/{f.Segment.Label}").Order(StringComparer.Ordinal).ToList();

    static IContainer Resolve(ReadOnlySpan<byte> bytes)
    {
        var format = FormatDetector.Detect(bytes);
        return Containers.FirstOrDefault(c => c.Format == format) ?? throw new UnsupportedFormatException();
    }
}

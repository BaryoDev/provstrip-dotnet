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
    static readonly IContainer[] Containers = [new PngContainer(), new JpegContainer(), new WebpContainer(), new SvgContainer()];

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
                case var o when ReferenceEquals(o, RuleOutcome.NoRule) && !container.KeepsUnmatched(segment):
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

        var known = before.Findings.ToHashSet();
        var removed = remove.Distinct().ToList();
        foreach (var f in removed)
        {
            if (!known.Contains(f))
                throw new ArgumentException($"{f.Description} at byte {f.Segment.Start} is not in this file", nameof(remove));
            if (!f.CanDelete)
                throw new ArgumentException($"{f.Description} at byte {f.Segment.Start} cannot be removed safely", nameof(remove));
        }

        // A segment inside one that is removed goes with it (SVG elements nest), so it is reported as removed too.
        var inside = Inside(before.Findings, removed);

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

        removed.AddRange(inside);
        var expected = Keys(before.Findings.Except(removed));
        var actual = Keys(after.Findings);
        if (!expected.SequenceEqual(actual))
            throw new ProvStripException(
                $"verify after strip failed: expected [{string.Join(", ", expected)}], found [{string.Join(", ", actual)}]");

        return new StripResult(output, removed, after.Findings);
    }

    // Findings not in removed whose segment lies within a removed one. Removed segments sorted by start with a
    // running maximum end answer "does any removed segment starting at or before this one reach past its end"
    // with one binary search, so this is O(n log n) rather than findings times removed.
    static List<Finding> Inside(IReadOnlyList<Finding> findings, List<Finding> removed)
    {
        var removedSet = removed.ToHashSet();
        var starts = removed.Select(r => r.Segment.Start).ToArray();
        var ends = removed.Select(r => r.Segment.End).ToArray();
        Array.Sort(starts, ends);
        for (var i = 1; i < ends.Length; i++) ends[i] = Math.Max(ends[i], ends[i - 1]);

        var inside = new List<Finding>();
        foreach (var f in findings)
        {
            if (removedSet.Contains(f)) continue;
            // Last index whose start is <= f's start.
            var at = Array.BinarySearch(starts, f.Segment.Start);
            if (at < 0) at = ~at - 1;
            else while (at + 1 < starts.Length && starts[at + 1] == f.Segment.Start) at++;
            if (at >= 0 && ends[at] >= f.Segment.End) inside.Add(f);
        }
        return inside;
    }

    static List<string> Keys(IEnumerable<Finding> findings) =>
        findings.Select(f => $"{f.RuleId}/{f.Category}/{f.Segment.Label}").Order(StringComparer.Ordinal).ToList();

    static IContainer Resolve(ReadOnlySpan<byte> bytes)
    {
        var format = FormatDetector.Detect(bytes);
        return Containers.FirstOrDefault(c => c.Format == format) ?? throw new UnsupportedFormatException();
    }
}

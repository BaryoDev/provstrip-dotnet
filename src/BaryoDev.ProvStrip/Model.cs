namespace BaryoDev.ProvStrip;

/// <summary>A byte range in the original file and what it is.</summary>
public sealed record Segment(int Start, int Length, string Label, string? Keyword = null)
{
    public int End => Start + Length;
}

public enum Category { Provenance, Camera, Unknown }

public enum CategoryMode { Auto, Flag }

/// <summary>Something found in a file that a person may want removed.</summary>
public sealed record Finding(string Format, Segment Segment, Category Category, string RuleId, string Description, bool CanDelete);

public sealed record Inspection(string Format, IReadOnlyList<Finding> Findings)
{
    public bool IsClean => Findings.Count == 0;
}

public class ProvStripException(string message) : Exception(message);

/// <summary>The file is broken or not what its signature says.</summary>
public sealed class ProvStripFormatException(string message, int offset)
    : ProvStripException($"{message} at byte {offset}")
{
    public int Offset { get; } = offset;
}

public sealed class UnsupportedFormatException()
    : ProvStripException("not a PNG, JPEG, WebP or SVG file");

/// <summary>A segment matched neither the rules nor the safe list, and the caller did not keep unknowns.</summary>
public sealed class UnknownSegmentException(IReadOnlyList<Finding> unknown)
    : ProvStripException(string.Join("; ", unknown.Select(f =>
        $"unknown {f.Segment.Label} segment at byte {f.Segment.Start}, refusing to guess")))
{
    public IReadOnlyList<Finding> Unknown { get; } = unknown;
}

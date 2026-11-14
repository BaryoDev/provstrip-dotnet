using System.Text;
using System.Text.Json;

namespace BaryoDev.ProvStrip.Tool;

/// <summary>What happened to one file, for the text line or the JSON entry.</summary>
internal sealed record FileReport(string Path)
{
    public string? Format { get; init; }
    public IReadOnlyList<Finding> Removed { get; init; } = [];
    public IReadOnlyList<Finding> Flagged { get; init; } = [];
    public IReadOnlyList<Finding> Unknown { get; init; } = [];
    public bool UnknownBlocked { get; init; }
    public string? Error { get; init; }

    /// <summary>0 when nothing asked about is left, 1 when findings were only flagged, 2 when something stopped.</summary>
    public int Code(bool verify) =>
        Error is not null || UnknownBlocked ? 2
        : verify ? (Flagged.Count + Unknown.Count > 0 ? 2 : 0)
        : Flagged.Count > 0 ? 1 : 0;
}

internal static class Report
{
    static string Name(Category c) => c switch
    {
        Category.Provenance => "AI attribution",
        Category.Camera => "camera data",
        _ => "unknown",
    };

    static string Descriptions(IEnumerable<Finding> findings) => string.Join(", ", findings.Select(f => f.Description).Distinct());

    public static string Line(FileReport r, bool verify)
    {
        if (r.Error is not null) return $"{r.Path}: {r.Error}";
        if (r.UnknownBlocked)
            return $"{r.Path}: " + string.Join("; ", r.Unknown.Select(f =>
                $"unknown {f.Segment.Label} segment at byte {f.Segment.Start}, refusing to guess")) +
                ". Re-run with --keep-unknown to keep it.";

        var parts = new List<string>();
        if (r.Removed.Count > 0) parts.Add($"stripped {Descriptions(r.Removed)}");
        foreach (var group in r.Flagged.GroupBy(f => f.Category))
            parts.Add($"{(verify ? "found" : "flagged")} {Name(group.Key)} ({Descriptions(group)})");
        if (r.Unknown.Count > 0)
            parts.Add((verify ? "found " : "kept ") + string.Join(", ", r.Unknown.Select(f => $"unknown {f.Segment.Label} segment at byte {f.Segment.Start}")));
        if (parts.Count == 0) parts.Add(verify ? "clean" : "already clean");
        return $"{r.Path}: {string.Join("; ", parts)}";
    }

    public static string Json(IReadOnlyList<FileReport> reports)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("files");
            foreach (var r in reports)
            {
                w.WriteStartObject();
                w.WriteString("path", r.Path);
                if (r.Format is null) w.WriteNull("format"); else w.WriteString("format", r.Format);
                Findings(w, "removed", r.Removed);
                Findings(w, "flagged", r.Flagged);
                Findings(w, "unknown", r.Unknown);
                if (r.Error is null) w.WriteNull("error"); else w.WriteString("error", r.Error);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Findings(Utf8JsonWriter w, string name, IReadOnlyList<Finding> findings)
    {
        w.WriteStartArray(name);
        foreach (var f in findings)
        {
            w.WriteStartObject();
            w.WriteString("ruleId", f.RuleId);
            w.WriteString("category", f.Category.ToString().ToLowerInvariant());
            w.WriteString("description", f.Description);
            w.WriteString("label", f.Segment.Label);
            w.WriteNumber("offset", f.Segment.Start);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

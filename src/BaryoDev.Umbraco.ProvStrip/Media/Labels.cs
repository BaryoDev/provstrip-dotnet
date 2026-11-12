using BaryoDev.ProvStrip;
using BaryoDev.Umbraco.ProvStrip.Data;

namespace BaryoDev.Umbraco.ProvStrip.Media;

public static class Labels
{
    /// <summary>"AI attribution found (C2PA)" and "Camera data found (GPS, camera model)", one per category with open findings.</summary>
    public static IReadOnlyList<string> For(IEnumerable<FindingRecord> findings)
    {
        var open = findings.Where(f => f.Status == FindingStatus.Open).ToList();
        var labels = new List<string>(2);
        foreach (var (category, text) in new[] { (Category.Provenance, "AI attribution found"), (Category.Camera, "Camera data found") })
        {
            var what = open.Where(f => f.Category == category).Select(f => f.Description).Distinct().ToList();
            if (what.Count > 0) labels.Add($"{text} ({string.Join(", ", what)})");
        }
        return labels;
    }
}

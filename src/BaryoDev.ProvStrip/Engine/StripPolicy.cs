namespace BaryoDev.ProvStrip.Engine;

/// <summary>What happens to each category. Unknown segments are never removed by a policy.</summary>
public sealed record StripPolicy(
    CategoryMode Provenance = CategoryMode.Flag,
    CategoryMode Camera = CategoryMode.Flag,
    bool KeepUnknown = false)
{
    public Decision Decide(Inspection inspection)
    {
        var remove = new List<Finding>();
        var flagged = new List<Finding>();
        var unknown = new List<Finding>();
        foreach (var f in inspection.Findings)
        {
            var mode = f.Category switch
            {
                Category.Provenance => Provenance,
                Category.Camera => Camera,
                _ => (CategoryMode?)null,
            };
            (mode switch
            {
                CategoryMode.Auto => remove,
                CategoryMode.Flag => flagged,
                _ => unknown,
            }).Add(f);
        }
        return new Decision(remove, flagged, unknown, KeepUnknown);
    }
}

/// <summary>A policy applied to one inspection.</summary>
public sealed record Decision(IReadOnlyList<Finding> Remove, IReadOnlyList<Finding> Flagged, IReadOnlyList<Finding> Unknown)
{
    readonly bool keepUnknown;

    internal Decision(IReadOnlyList<Finding> remove, IReadOnlyList<Finding> flagged, IReadOnlyList<Finding> unknown, bool keepUnknown)
        : this(remove, flagged, unknown) => this.keepUnknown = keepUnknown;

    /// <summary>True when an unknown segment was found and the policy does not keep unknowns.</summary>
    public bool Blocked => Unknown.Count > 0 && !keepUnknown;
}

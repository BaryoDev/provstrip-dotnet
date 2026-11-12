namespace BaryoDev.ProvStrip.Rules;

/// <summary>What the rules say about one segment.</summary>
internal abstract record RuleOutcome
{
    public sealed record Match(Category Category, string RuleId, string Description) : RuleOutcome;

    sealed record Named(string Name) : RuleOutcome;

    /// <summary>A rule matched and said the segment is fine to keep.</summary>
    public static readonly RuleOutcome Keep = new Named("keep");

    /// <summary>No rule matched.</summary>
    public static readonly RuleOutcome NoRule = new Named("no rule");
}

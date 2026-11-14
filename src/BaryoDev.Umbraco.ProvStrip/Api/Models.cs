namespace BaryoDev.Umbraco.ProvStrip.Api;

public sealed record FindingModel(string Category, string RuleId, string Description, string Status);

/// <summary>What is known about one media item: the labels a person sees, every finding, and the file's hash.</summary>
public sealed record MediaFindingsModel(Guid Key, string? Hash, IReadOnlyList<string> Labels, IReadOnlyList<FindingModel> Findings, string? NeedsLook);

public sealed class RemoveRequestModel
{
    /// <summary>"Provenance" or "Camera"; leave out for both.</summary>
    public string? Category { get; set; }

    /// <summary>The hash from the check this removal is based on. A different file gets 409.</summary>
    public string Hash { get; set; } = "";
}

public sealed class SkipRequestModel
{
    public string Category { get; set; } = "";

    public string Hash { get; set; } = "";
}

public sealed class RemoveAllRequestModel
{
    /// <summary>"Provenance" or "Camera"; leave out for both.</summary>
    public string? Category { get; set; }
}

/// <summary>State is None before the first scan, then Requested, Running, Done or Cancelled.</summary>
public sealed record ScanModel(string State, int Total, int Checked, int WithFindings, DateTime? StartedUtc, DateTime? FinishedUtc);

/// <summary>One item in the scan results. Folder is the path of folder names above it, "" at the root.</summary>
public sealed record MediaFindingsSummaryModel(Guid Key, string Name, string Folder, IReadOnlyList<string> Labels, string? NeedsLook);

public sealed record PagedResultsModel(int Total, IReadOnlyList<MediaFindingsSummaryModel> Items);

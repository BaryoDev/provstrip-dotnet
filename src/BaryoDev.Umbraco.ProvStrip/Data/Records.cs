using BaryoDev.ProvStrip;

namespace BaryoDev.Umbraco.ProvStrip.Data;

public enum FindingStatus { Open, Skipped, Removed, NeedsLook }

public enum ScanState { Requested, Running, Done, Cancelled }

/// <summary>One finding on one media item, for one version of its file (the hash).</summary>
public sealed record FindingRecord(Guid MediaKey, string ContentHash, Category Category, string RuleId, string Description, FindingStatus Status);

public sealed record MediaFindingsSummary(Guid MediaKey, IReadOnlyList<FindingRecord> Findings);

public sealed record FindingPage(IReadOnlyList<MediaFindingsSummary> Items, int Total);

public sealed record ScanRecord(int Id, ScanState State, int CursorId, int Total, int Checked, int WithFindings, DateTime? StartedUtc, DateTime? FinishedUtc);

public sealed class ScanRunningException() : InvalidOperationException("A scan is already running.");

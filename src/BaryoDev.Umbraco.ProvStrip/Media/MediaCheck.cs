using BaryoDev.Umbraco.ProvStrip.Data;

namespace BaryoDev.Umbraco.ProvStrip.Media;

/// <summary>What a check found on one media item. NeedsLook says why a person should look at it.</summary>
public sealed record MediaCheck(Guid MediaKey, string? Path, string? Hash, IReadOnlyList<FindingRecord> Findings, string? NeedsLook);

/// <summary>The file changed since the hash a Delete was asked for.</summary>
public sealed class StaleFileException() : InvalidOperationException("The file changed since it was checked. Check it again.");

/// <summary>Set while this package saves a media item itself, so the upload handler leaves that save alone.</summary>
internal static class ReentryGuard
{
    static readonly AsyncLocal<bool> Active = new();

    public static bool IsActive => Active.Value;

    public static IDisposable Enter()
    {
        Active.Value = true;
        return new Exit();
    }

    sealed class Exit : IDisposable
    {
        public void Dispose() => Active.Value = false;
    }
}

using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.ProvStrip.Data;

public interface IScanStore
{
    Task<ScanRecord?> CurrentAsync();

    /// <summary>Queues a scan. Throws <see cref="ScanRunningException"/> while one is requested or running.</summary>
    Task<ScanRecord> RequestAsync();

    Task StartAsync(int scanId, int total);

    Task AdvanceAsync(int scanId, int cursorId, int checkedDelta, int withFindingsDelta);

    Task FinishAsync(int scanId);
}

internal sealed class ScanStore(IScopeProvider scopes) : IScanStore
{
    const string Table = ScanRow.TableName;

    public Task<ScanRecord?> CurrentAsync()
    {
        using var scope = scopes.CreateScope(autoComplete: true);
        var row = scope.Database.FirstOrDefault<ScanRow>($"SELECT * FROM {Table} ORDER BY Id DESC");
        return Task.FromResult(row is null ? null : ToRecord(row));
    }

    // One statement that writes first. A read then a write in one SQLite transaction can deadlock against
    // another transaction doing the same, and NPoco's default command timeout waits forever.
    public Task<ScanRecord> RequestAsync()
    {
        using var scope = scopes.CreateScope();
        var inserted = scope.Database.Execute(
            $"INSERT INTO {Table} (State, CursorId, Total, Checked, WithFindings) SELECT @0, 0, 0, 0, 0 " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {Table} WHERE State IN (@1, @2))",
            nameof(ScanState.Requested), nameof(ScanState.Requested), nameof(ScanState.Running));
        // Faulted task rather than a throw, so callers that await or hold the task both see it.
        if (inserted == 0) return Task.FromException<ScanRecord>(new ScanRunningException());
        var row = scope.Database.First<ScanRow>($"SELECT * FROM {Table} ORDER BY Id DESC");
        scope.Complete();
        return Task.FromResult(ToRecord(row));
    }

    public Task StartAsync(int scanId, int total) => Execute(
        $"UPDATE {Table} SET State = @0, Total = @1, StartedUtc = @2 WHERE Id = @3", nameof(ScanState.Running), total, DateTime.UtcNow, scanId);

    public Task AdvanceAsync(int scanId, int cursorId, int checkedDelta, int withFindingsDelta) => Execute(
        $"UPDATE {Table} SET CursorId = @0, Checked = Checked + @1, WithFindings = WithFindings + @2 WHERE Id = @3",
        cursorId, checkedDelta, withFindingsDelta, scanId);

    public Task FinishAsync(int scanId) => Execute(
        $"UPDATE {Table} SET State = @0, FinishedUtc = @1 WHERE Id = @2", nameof(ScanState.Done), DateTime.UtcNow, scanId);

    Task Execute(string sql, params object[] args)
    {
        using var scope = scopes.CreateScope();
        scope.Database.Execute(sql, args);
        scope.Complete();
        return Task.CompletedTask;
    }

    static ScanRecord ToRecord(ScanRow r) =>
        new(r.Id, Enum.Parse<ScanState>(r.State), r.CursorId, r.Total, r.Checked, r.WithFindings, r.StartedUtc, r.FinishedUtc);
}

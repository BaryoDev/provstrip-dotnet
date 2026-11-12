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

    public Task<ScanRecord> RequestAsync()
    {
        using var scope = scopes.CreateScope();
        var busy = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Table} WHERE State IN (@0, @1)",
            nameof(ScanState.Requested), nameof(ScanState.Running));
        // Faulted task rather than a throw, so callers that await or hold the task both see it.
        if (busy > 0) return Task.FromException<ScanRecord>(new ScanRunningException());
        var row = new ScanRow { State = nameof(ScanState.Requested) };
        scope.Database.Insert(row);
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

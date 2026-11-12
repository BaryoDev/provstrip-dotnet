using BaryoDev.ProvStrip;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.ProvStrip.Data;

public interface IFindingStore
{
    Task<IReadOnlyList<FindingRecord>> ForMediaAsync(Guid mediaKey);

    /// <summary>Replaces the item's rows. A (hash, category) already skipped stays skipped.</summary>
    Task ReplaceAsync(Guid mediaKey, string hash, IReadOnlyList<FindingRecord> findings);

    Task SkipAsync(Guid mediaKey, string hash, Category category);

    Task<bool> IsSkippedAsync(string hash, Category category);

    /// <summary>Media with Open or NeedsLook rows, ordered by key.</summary>
    Task<FindingPage> OpenAsync(int skip, int take);
}

internal sealed class FindingStore(IScopeProvider scopes) : IFindingStore
{
    const string Table = FindingRow.TableName;

    public Task<IReadOnlyList<FindingRecord>> ForMediaAsync(Guid mediaKey)
    {
        using var scope = scopes.CreateScope(autoComplete: true);
        IReadOnlyList<FindingRecord> rows = scope.Database
            .Fetch<FindingRow>($"SELECT * FROM {Table} WHERE MediaKey = @0 ORDER BY Id", mediaKey)
            .Select(ToRecord).ToList();
        return Task.FromResult(rows);
    }

    public Task ReplaceAsync(Guid mediaKey, string hash, IReadOnlyList<FindingRecord> findings)
    {
        // The skip lookup reads in its own scope, so the write scope below starts with a write. A read then a
        // write in one SQLite transaction can deadlock against another doing the same.
        HashSet<string> skipped;
        using (var read = scopes.CreateScope(autoComplete: true))
            skipped = read.Database
                .Fetch<string>($"SELECT DISTINCT Category FROM {Table} WHERE ContentHash = @0 AND Status = @1", hash, nameof(FindingStatus.Skipped))
                .ToHashSet(StringComparer.Ordinal);

        using var scope = scopes.CreateScope();
        scope.Database.Execute($"DELETE FROM {Table} WHERE MediaKey = @0", mediaKey);
        var now = DateTime.UtcNow;
        foreach (var f in findings)
        {
            var status = f.Status == FindingStatus.Open && skipped.Contains(f.Category.ToString()) ? FindingStatus.Skipped : f.Status;
            scope.Database.Insert(ToRow(f with { MediaKey = mediaKey, ContentHash = hash, Status = status }, now));
        }
        scope.Complete();
        return Task.CompletedTask;
    }

    public Task SkipAsync(Guid mediaKey, string hash, Category category)
    {
        using var scope = scopes.CreateScope();
        var changed = scope.Database.Execute(
            $"UPDATE {Table} SET Status = @0, UpdatedUtc = @1 WHERE MediaKey = @2 AND ContentHash = @3 AND Category = @4 AND Status IN (@5, @6)",
            nameof(FindingStatus.Skipped), DateTime.UtcNow, mediaKey, hash, category.ToString(), nameof(FindingStatus.Open), nameof(FindingStatus.Skipped));
        // Nothing open to mark: keep a marker row so the skip still holds for this hash.
        if (changed == 0)
            scope.Database.Insert(ToRow(new FindingRecord(mediaKey, hash, category, "skip", "", FindingStatus.Skipped), DateTime.UtcNow));
        scope.Complete();
        return Task.CompletedTask;
    }

    public Task<bool> IsSkippedAsync(string hash, Category category)
    {
        using var scope = scopes.CreateScope(autoComplete: true);
        var n = scope.Database.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM {Table} WHERE ContentHash = @0 AND Category = @1 AND Status = @2", hash, category.ToString(), nameof(FindingStatus.Skipped));
        return Task.FromResult(n > 0);
    }

    public Task<FindingPage> OpenAsync(int skip, int take)
    {
        using var scope = scopes.CreateScope(autoComplete: true);
        const string where = "Status IN (@0, @1)";
        object[] args = [nameof(FindingStatus.Open), nameof(FindingStatus.NeedsLook)];
        var total = scope.Database.ExecuteScalar<int>($"SELECT COUNT(DISTINCT MediaKey) FROM {Table} WHERE {where}", args);
        var keys = scope.Database.SkipTake<Guid>(skip, take, $"SELECT DISTINCT MediaKey FROM {Table} WHERE {where} ORDER BY MediaKey", args);
        var items = new List<MediaFindingsSummary>(keys.Count);
        foreach (var key in keys)
            items.Add(new MediaFindingsSummary(key, scope.Database
                .Fetch<FindingRow>($"SELECT * FROM {Table} WHERE MediaKey = @0 ORDER BY Id", key).Select(ToRecord).ToList()));
        return Task.FromResult(new FindingPage(items, total));
    }

    static FindingRecord ToRecord(FindingRow r) => new(r.MediaKey, r.ContentHash, Enum.Parse<Category>(r.Category), r.RuleId, r.Description, Enum.Parse<FindingStatus>(r.Status));

    static FindingRow ToRow(FindingRecord f, DateTime now) => new()
    {
        MediaKey = f.MediaKey,
        ContentHash = f.ContentHash,
        Category = f.Category.ToString(),
        RuleId = f.RuleId.Length > 64 ? f.RuleId[..64] : f.RuleId,
        Description = f.Description.Length > 255 ? f.Description[..255] : f.Description,
        Status = f.Status.ToString(),
        UpdatedUtc = now,
    };
}

using System.Threading.Channels;
using BaryoDev.ProvStrip;
using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ProvStrip.Api;

/// <summary>Remove-all requests waiting for <see cref="RemoveAllWorker"/>. Null means both categories.</summary>
public sealed class RemoveAllQueue
{
    readonly Channel<Category?> channel = Channel.CreateUnbounded<Category?>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Category? category) => channel.Writer.TryWrite(category);

    internal ChannelReader<Category?> Reader => channel.Reader;
}

/// <summary>Removes a category from every item with an Open finding in it, one item at a time, through RemoveAsync.</summary>
public sealed class RemoveAllWorker(RemoveAllQueue queue, IFindingStore findings, IMediaProvStrip strip, ILogger<RemoveAllWorker> logger) : BackgroundService
{
    const int PageSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var category in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var removed = await RunAsync(category, stoppingToken);
                logger.LogInformation("ProvStrip removed {Category} from {Count} media items", category?.ToString() ?? "everything", removed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "ProvStrip remove-all failed");
            }
        }
    }

    /// <summary>Returns how many items were cleaned. An item whose file changed since its check is left for the next check.</summary>
    public async Task<int> RunAsync(Category? category, CancellationToken ct)
    {
        // Collect first: each removal changes the Open set the pages are cut from.
        var targets = new List<(Guid Key, string Hash)>();
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await findings.OpenAsync(skip, PageSize);
            foreach (var item in page.Items)
                if (item.Findings.FirstOrDefault(f => f.Status == FindingStatus.Open && (category is null ? f.Category != Category.Unknown : f.Category == category)) is { } open)
                    targets.Add((item.MediaKey, open.ContentHash));
            if (page.Items.Count < PageSize) break;
        }

        var removed = 0;
        foreach (var (key, hash) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await strip.RemoveAsync(key, category, hash, ct);
                removed++;
            }
            catch (Exception e) when (e is StaleFileException or KeyNotFoundException)
            {
                logger.LogInformation("ProvStrip skipped {MediaKey}: {Reason}", key, e.Message);
            }
        }
        return removed;
    }
}

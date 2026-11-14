using System.Security.Cryptography;
using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Engine;
using BaryoDev.ProvStrip.Formats;
using BaryoDev.Umbraco.ProvStrip.Data;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.ProvStrip.Media;

public interface IMediaProvStrip
{
    /// <summary>Reads the item's file, records what is in it, and changes nothing.</summary>
    Task<MediaCheck> CheckAsync(IMedia media, CancellationToken ct);

    /// <summary>
    /// Strips one category (or both when null) from the stored file in place. Throws <see cref="StaleFileException"/>
    /// when the file's hash is no longer <paramref name="expectedHash"/>.
    /// </summary>
    Task<MediaCheck> RemoveAsync(Guid mediaKey, Category? category, string expectedHash, CancellationToken ct);
}

internal sealed class MediaProvStrip(
    IOptions<ProvStripOptions> options,
    IFindingStore store,
    MediaFileManager files,
    MediaUrlGeneratorCollection urlGenerators,
    IMediaService mediaService,
    IIdKeyMap idKeyMap) : IMediaProvStrip
{
    const int SniffBytes = 4096;

    public Task<MediaCheck> CheckAsync(IMedia media, CancellationToken ct) => CheckCoreAsync(media, autoStrip: false);

    /// <summary>
    /// The upload check: like <see cref="CheckAsync"/>, but strips the categories set to Auto from the stored file and
    /// sets the size on <paramref name="media"/>, which the caller is saving. Never saves.
    /// </summary>
    internal Task<MediaCheck> CheckUploadAsync(IMedia media, CancellationToken ct) => CheckCoreAsync(media, autoStrip: true);

    async Task<MediaCheck> CheckCoreAsync(IMedia media, bool autoStrip)
    {
        var read = Read(media);
        if (read.Skip) return new MediaCheck(media.Key, read.Path, null, [], null);
        if (read.Problem is { } problem) return await NeedsLook(media.Key, read.Path, read.Hash ?? "", problem);

        if (autoStrip && await AutoRemovalsAsync(read) is { Count: > 0 } remove) return await StripAsync(media, read, remove);

        var records = Analyze(media.Key, read.Hash!, read.Bytes!, out var unknown);
        if (unknown is not null) return await NeedsLook(media.Key, read.Path, read.Hash!, unknown, records);
        await store.ReplaceAsync(media.Key, read.Hash!, records);
        return new MediaCheck(media.Key, read.Path, read.Hash, await store.ForMediaAsync(media.Key), null);
    }

    public async Task<MediaCheck> RemoveAsync(Guid mediaKey, Category? category, string expectedHash, CancellationToken ct)
    {
        // By id: IMediaService.GetById(Guid) is binary-incompatible between Umbraco 17 and 18, GetById(int) is not.
        var id = idKeyMap.GetIdForKey(mediaKey, UmbracoObjectTypes.Media);
        var media = (id.Success ? mediaService.GetById(id.Result) : null) ?? throw new KeyNotFoundException($"media {mediaKey} not found");
        var read = Read(media);
        if (read.Bytes is null || read.Hash != expectedHash)
        {
            // Record the file as it is now, so the next look shows what is really there, not the finding that went stale.
            await CheckAsync(media, ct);
            throw new StaleFileException();
        }

        var policy = new StripPolicy(
            category is null or Category.Provenance ? CategoryMode.Auto : CategoryMode.Flag,
            category is null or Category.Camera ? CategoryMode.Auto : CategoryMode.Flag,
            KeepUnknown: true);
        var decision = policy.Decide(ProvStripEngine.Inspect(read.Bytes));
        var remove = new List<Finding>(decision.Remove.Count);
        foreach (var f in decision.Remove)
            // Without a category this is "remove everything", which must not undo a person's Skip.
            if (category is not null || !await store.IsSkippedAsync(read.Hash, f.Category)) remove.Add(f);
        if (remove.Count == 0) return await CheckAsync(media, ct);

        var check = await StripAsync(media, read, remove);
        using (ReentryGuard.Enter()) mediaService.Save(media);
        return check;
    }

    /// <summary>The findings the options say to strip on upload, less any category a person skipped for this file.</summary>
    async Task<List<Finding>> AutoRemovalsAsync(FileRead read)
    {
        var o = options.Value;
        if (o.Provenance != CategoryMode.Auto && o.Camera != CategoryMode.Auto) return [];
        Decision decision;
        try
        {
            decision = new StripPolicy(o.Provenance, o.Camera, o.KeepUnknown).Decide(ProvStripEngine.Inspect(read.Bytes!));
        }
        catch (ProvStripException)
        {
            return [];
        }
        // An unknown segment the options do not keep means a person looks first; nothing is stripped.
        if (decision.Blocked) return [];
        var remove = new List<Finding>(decision.Remove.Count);
        foreach (var f in decision.Remove)
            if (!await store.IsSkippedAsync(read.Hash!, f.Category)) remove.Add(f);
        return remove;
    }

    /// <summary>Overwrites the stored file without <paramref name="remove"/>, sets the size on the entity, and records the result.</summary>
    async Task<MediaCheck> StripAsync(IMedia media, FileRead read, IReadOnlyList<Finding> remove)
    {
        StripResult result;
        try
        {
            result = ProvStripEngine.Strip(read.Bytes!, remove);
        }
        catch (ProvStripException e)
        {
            return await NeedsLook(media.Key, read.Path, read.Hash!, $"could not be cleaned: {e.Message}",
                Analyze(media.Key, read.Hash!, read.Bytes!, out _));
        }
        using (var output = new MemoryStream(result.Output, writable: false))
            files.FileSystem.AddFile(read.Relative!, output, overrideIfExists: true);
        // A custom media type may have no size property; SetValue would throw after the file was already written.
        if (media.HasProperty(Constants.Conventions.Media.Bytes)) media.SetValue(Constants.Conventions.Media.Bytes, result.Output.Length);

        // Skips belong to the old hash. Carry them to the cleaned file, so removing one category does not bring back
        // the label a person skipped on another.
        var skipped = new List<Category>();
        foreach (var c in Enum.GetValues<Category>())
            if (await store.IsSkippedAsync(read.Hash!, c)) skipped.Add(c);

        var hash = Hash(result.Output);
        var records = Analyze(media.Key, hash, result.Output, out var unknown).Concat(
            result.Removed.Select(f => new FindingRecord(media.Key, hash, f.Category, f.RuleId, f.Description, FindingStatus.Removed))).ToList();
        await store.ReplaceAsync(media.Key, hash, records);
        foreach (var c in skipped.Where(c => records.Any(r => r.Category == c && r.Status is FindingStatus.Open or FindingStatus.NeedsLook)))
            await store.SkipAsync(media.Key, hash, c);
        return new MediaCheck(media.Key, read.Path, hash, await store.ForMediaAsync(media.Key), unknown);
    }

    sealed record FileRead(string? Path, string? Relative, byte[]? Bytes, string? Hash, string? Problem, bool Skip);

    FileRead Read(IMedia media)
    {
        if (!media.TryGetMediaPath(Constants.Conventions.Media.File, urlGenerators, out var path) || string.IsNullOrEmpty(path))
            return new FileRead(null, null, null, null, null, Skip: true);
        var relative = files.FileSystem.GetRelativePath(path);
        if (!files.FileSystem.FileExists(relative)) return new FileRead(path, relative, null, null, "file not found", false);
        try
        {
            using var stream = files.FileSystem.OpenFile(relative);
            var head = new byte[SniffBytes];
            var got = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (FormatDetector.Detect(head.AsSpan(0, got)) is null) return new FileRead(path, relative, null, null, null, Skip: true);
            var size = files.FileSystem.GetSize(relative);
            if (size > options.Value.MaxFileBytes) return new FileRead(path, relative, null, null, "too large to check", false);

            using var all = new MemoryStream((int)size);
            all.Write(head, 0, got);
            stream.CopyTo(all);
            var bytes = all.ToArray();
            return new FileRead(path, relative, bytes, Hash(bytes), null, false);
        }
        catch (IOException e)
        {
            return new FileRead(path, relative, null, null, $"file could not be read: {e.Message}", false);
        }
    }

    static List<FindingRecord> Analyze(Guid key, string hash, byte[] bytes, out string? unknown)
    {
        unknown = null;
        Inspection inspection;
        try
        {
            inspection = ProvStripEngine.Inspect(bytes);
        }
        catch (ProvStripException e)
        {
            unknown = $"could not be read: {e.Message}";
            return [];
        }
        var records = new List<FindingRecord>(inspection.Findings.Count);
        foreach (var f in inspection.Findings)
        {
            var status = f.Category == Category.Unknown ? FindingStatus.NeedsLook : FindingStatus.Open;
            if (status == FindingStatus.NeedsLook) unknown ??= f.Description;
            records.Add(new FindingRecord(key, hash, f.Category, f.RuleId, f.Description, status));
        }
        return records;
    }

    async Task<MediaCheck> NeedsLook(Guid key, string? path, string hash, string reason, IReadOnlyList<FindingRecord>? records = null)
    {
        records ??= [new FindingRecord(key, hash, Category.Unknown, "needs-look", reason, FindingStatus.NeedsLook)];
        await store.ReplaceAsync(key, hash, records);
        return new MediaCheck(key, path, hash.Length == 0 ? null : hash, await store.ForMediaAsync(key), reason);
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

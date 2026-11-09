using System.IO.Compression;
using LANCommander.SDK.Chunking;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Services.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace LANCommander.Server.Services.Chunks;

/// <summary>
/// Converts archives to chunk manifests: reads the zip, stores every chunk and the manifest itself in
/// the <see cref="ChunkStore"/>, records the chunks and links them to the archive, and keeps the
/// archive's <see cref="ChunkManifestStatus"/> current. The zip is never changed.
/// </summary>
public sealed class ChunkManifestService(
    ArchiveService archiveService,
    ChunkStore chunkStore,
    ChunkStoreGate gate,
    IDbContextFactory<DatabaseContext> contextFactory,
    IFusionCache cache,
    ILogger<ChunkManifestService> logger)
{
    /// <summary>How many hashes go in one database lookup; SQLite allows a limited number of parameters.</summary>
    private const int LookupBatchSize = 500;

    /// <summary>Marks an archive as queued for conversion, so the admin UI shows it before the job starts.</summary>
    public Task MarkPendingAsync(Guid archiveId) =>
        SetStatusAsync(archiveId, ChunkManifestStatus.Pending, error: null);

    /// <summary>
    /// Converts the archive. On success it's <see cref="ChunkManifestStatus.Ready"/> with its manifest
    /// hash; on any problem with the archive itself (missing, damaged, unsafe paths) it's
    /// <see cref="ChunkManifestStatus.Failed"/> with the reason, and the previous manifest's links are
    /// left as they were. Returns the final status.
    /// </summary>
    public async Task<ChunkManifestStatus> BuildAsync(Guid archiveId, CancellationToken cancellationToken = default)
    {
        var archive = await archiveService.AsNoTracking().GetAsync(archiveId);

        if (archive == null)
            throw new KeyNotFoundException($"Archive {archiveId} doesn't exist");

        await SetStatusAsync(archiveId, ChunkManifestStatus.Pending, error: null);

        // From the first chunk written to the last link recorded, so cleanup can't remove a chunk
        // this conversion found already stored and is about to link
        using var conversion = await gate.EnterConversionAsync(cancellationToken);

        var path = await archiveService.GetArchiveFileLocationAsync(archive);

        if (!File.Exists(path))
            return await FailAsync(archiveId, $"The archive file is missing: {path}");

        ChunkManifest manifest;
        string manifestHash;
        var chunks = new Dictionary<string, StoredChunkInfo>(StringComparer.Ordinal);

        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                manifest = await ZipChunker.BuildManifestAsync(zip, async (chunk, token) =>
                {
                    var stored = await chunkStore.WriteAsync(chunk.Hash, chunk.Data, token);

                    chunks[chunk.Hash] = new StoredChunkInfo(chunk.Data.Length, stored.StoredSize, stored.Compression);
                }, cancellationToken: cancellationToken);
            }

            // The manifest is stored like a chunk, named by the hash of its exact bytes
            var serialized = ChunkManifestSerializer.Serialize(manifest);

            manifestHash = ChunkManifestSerializer.ComputeHash(serialized);

            var storedManifest = await chunkStore.WriteAsync(manifestHash, serialized, cancellationToken);

            chunks[manifestHash] = new StoredChunkInfo(serialized.Length, storedManifest.StoredSize, storedManifest.Compression);
        }
        catch (Exception ex) when (ex is ChunkManifestException or InvalidDataException)
        {
            // Something wrong with the archive itself, which retrying won't fix
            return await FailAsync(archiveId, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk, a missing permission, or the zip being locked by something else
            return await FailAsync(archiveId, $"Reading the archive or writing the chunk store failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            await FailAsync(archiveId, "The conversion was cancelled");

            throw;
        }

        await RecordAsync(archiveId, manifestHash, chunks, cancellationToken);

        logger.LogInformation(
            "Archive {ArchiveId} converted to {FileCount} files in {ChunkCount} chunks, manifest {ManifestHash}",
            archiveId, manifest.Files.Count, chunks.Count - 1, manifestHash);

        return ChunkManifestStatus.Ready;
    }

    /// <summary>
    /// The archives waiting for conversion, oldest first: never converted, or out of date since their
    /// zip changed. Failed archives are left alone; an administrator rebuilds them once the cause is fixed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetArchivesToConvertAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<Archive>()
            .Where(a => a.ManifestStatus == ChunkManifestStatus.None || a.ManifestStatus == ChunkManifestStatus.Stale)
            .OrderBy(a => a.CreatedOn)
            .Select(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Archives left Pending by a conversion that never finished: queued jobs are lost when the server
    /// restarts. Each goes back to waiting, as Stale if it had a manifest before, so the sweep picks it
    /// up. Only call this when no conversion is running. Returns how many were reset.
    /// </summary>
    public async Task<int> ResetInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var interrupted = await context.Set<Archive>()
            .Where(a => a.ManifestStatus == ChunkManifestStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var archive in interrupted)
            archive.ManifestStatus = string.IsNullOrEmpty(archive.ManifestHash) ? ChunkManifestStatus.None : ChunkManifestStatus.Stale;

        await context.SaveChangesAsync(cancellationToken);

        foreach (var archive in interrupted)
            await cache.ExpireArchiveCacheAsync(archive.Id);

        return interrupted.Count;
    }

    /// <summary>How many archives are in each state, and how much is left to convert.</summary>
    public async Task<ChunkConversionProgress> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var counts = await context.Set<Archive>()
            .GroupBy(a => a.ManifestStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var remaining = await context.Set<Archive>()
            .Where(a => a.ManifestStatus == ChunkManifestStatus.None || a.ManifestStatus == ChunkManifestStatus.Stale)
            .SumAsync(a => a.UncompressedSize, cancellationToken);

        var stored = await context.Set<Chunk>().SumAsync(c => c.StoredSize, cancellationToken);

        return new ChunkConversionProgress(
            counts.ToDictionary(c => c.Status, c => c.Count),
            remaining,
            stored);
    }

    /// <summary>The archive's manifest, or null if it isn't converted.</summary>
    public async Task<ChunkManifest?> GetManifestAsync(Guid archiveId, CancellationToken cancellationToken = default)
    {
        var archive = await archiveService.AsNoTracking().GetAsync(archiveId);

        if (archive?.ManifestStatus != ChunkManifestStatus.Ready || string.IsNullOrEmpty(archive.ManifestHash))
            return null;

        var bytes = await chunkStore.ReadAsync(archive.ManifestHash, cancellationToken);

        return bytes == null ? null : ChunkManifestSerializer.Deserialize(bytes);
    }

    /// <summary>
    /// Records the chunks and replaces the archive's links in one save, so the archive is never left
    /// half-linked. Chunk files are already written by now; a crash before this leaves only unlinked
    /// files, which the cleanup job removes.
    /// </summary>
    private async Task RecordAsync(Guid archiveId, string manifestHash, IReadOnlyDictionary<string, StoredChunkInfo> chunks, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var batch in chunks.Keys.Chunk(LookupBatchSize))
        {
            var found = await context.Set<Chunk>()
                .Where(c => batch.Contains(c.Hash))
                .Select(c => c.Hash)
                .ToListAsync(cancellationToken);

            known.UnionWith(found);
        }

        var now = DateTime.UtcNow;

        foreach (var (hash, info) in chunks)
        {
            if (known.Contains(hash))
                continue;

            context.Set<Chunk>().Add(new Chunk
            {
                Hash = hash,
                Size = info.Size,
                StoredSize = info.StoredSize,
                Compression = info.Compression,
                CreatedOn = now,
            });
        }

        // Only the difference: chunks a rebuilt archive still uses keep their links
        var previous = await context.Set<ArchiveChunk>()
            .Where(ac => ac.ArchiveId == archiveId)
            .ToListAsync(cancellationToken);

        context.Set<ArchiveChunk>().RemoveRange(previous.Where(ac => !chunks.ContainsKey(ac.ChunkHash)));

        var linked = previous.Select(ac => ac.ChunkHash).ToHashSet(StringComparer.Ordinal);

        foreach (var hash in chunks.Keys.Where(h => !linked.Contains(h)))
            context.Set<ArchiveChunk>().Add(new ArchiveChunk { ArchiveId = archiveId, ChunkHash = hash });

        var archive = await context.Set<Archive>().FirstAsync(a => a.Id == archiveId, cancellationToken);

        archive.ManifestStatus = ChunkManifestStatus.Ready;
        archive.ManifestHash = manifestHash;
        archive.ManifestError = null;
        archive.ManifestBuiltOn = now;

        await context.SaveChangesAsync(cancellationToken);

        await cache.ExpireArchiveCacheAsync(archiveId);
    }

    private async Task<ChunkManifestStatus> FailAsync(Guid archiveId, string reason)
    {
        logger.LogWarning("Archive {ArchiveId} could not be converted to chunks: {Reason}", archiveId, reason);

        await SetStatusAsync(archiveId, ChunkManifestStatus.Failed, reason);

        return ChunkManifestStatus.Failed;
    }

    private async Task SetStatusAsync(Guid archiveId, ChunkManifestStatus status, string? error)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var archive = await context.Set<Archive>().FirstOrDefaultAsync(a => a.Id == archiveId)
            ?? throw new KeyNotFoundException($"Archive {archiveId} doesn't exist");

        archive.ManifestStatus = status;
        archive.ManifestError = error;

        await context.SaveChangesAsync();

        await cache.ExpireArchiveCacheAsync(archiveId);
    }

    private readonly record struct StoredChunkInfo(long Size, long StoredSize, ChunkCompression Compression);
}

/// <summary>The library's conversion to chunks at a moment.</summary>
/// <param name="Counts">How many archives are in each state; a state with none is missing.</param>
/// <param name="RemainingUncompressedSize">
/// The uncompressed size of the archives still to convert: the most the chunk store can grow by
/// converting them. Compression and chunks shared with archives already converted make it less.
/// </param>
/// <param name="StoredSize">What the chunk store holds now.</param>
public sealed record ChunkConversionProgress(
    IReadOnlyDictionary<ChunkManifestStatus, int> Counts,
    long RemainingUncompressedSize,
    long StoredSize)
{
    public int Count(ChunkManifestStatus status) => Counts.TryGetValue(status, out var count) ? count : 0;

    public int Remaining => Count(ChunkManifestStatus.None) + Count(ChunkManifestStatus.Stale);
}

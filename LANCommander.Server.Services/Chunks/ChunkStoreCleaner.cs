using LANCommander.SDK.Enums;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LANCommander.Server.Services.Chunks;

/// <summary>What a cleanup removed.</summary>
/// <param name="Skipped">True if it didn't run because a conversion was in progress.</param>
/// <param name="UnusedChunksRemoved">Chunks no archive linked to any more, removed with their files.</param>
/// <param name="LeftoverFilesRemoved">Files no chunk record names: written by a conversion that never finished.</param>
/// <param name="BytesFreed">The space both freed.</param>
public sealed record ChunkCleanupResult(bool Skipped, int UnusedChunksRemoved, int LeftoverFilesRemoved, long BytesFreed);

/// <summary>
/// Removes what the chunk store no longer needs: chunks that no archive links to (their archives
/// were deleted, or rebuilt without them), and files a conversion wrote but never recorded. Only
/// things older than a grace period go, and only while no conversion is running.
/// </summary>
public sealed class ChunkStoreCleaner(
    ChunkStore chunkStore,
    ChunkStoreGate gate,
    StorageLocationService storageLocationService,
    IDbContextFactory<DatabaseContext> contextFactory,
    ILogger<ChunkStoreCleaner> logger)
{
    /// <summary>What the scheduled cleanup leaves alone: anything newer than a day.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);

    private const int BatchSize = 500;

    private static readonly ChunkCleanupResult Nothing = new(false, 0, 0, 0);

    public async Task<ChunkCleanupResult> CleanUpAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        // A server that never used chunks has no store, and asking the store for its location would make one
        if (await storageLocationService.DefaultAsync(StorageLocationType.Chunk) == null)
            return Nothing;

        using var turn = await gate.TryEnterCleanupAsync();

        if (turn == null)
        {
            logger.LogInformation("Chunk store cleanup skipped: a conversion is running");

            return Nothing with { Skipped = true };
        }

        var cutoff = DateTime.UtcNow - grace;

        var (chunksRemoved, chunkBytes) = await RemoveUnusedChunksAsync(cutoff, cancellationToken);
        var (filesRemoved, fileBytes) = await RemoveLeftoverFilesAsync(cutoff, cancellationToken);

        if (chunksRemoved > 0 || filesRemoved > 0)
            logger.LogInformation(
                "Chunk store cleanup removed {Chunks} unused chunk(s) and {Files} leftover file(s), freeing {Bytes} bytes",
                chunksRemoved, filesRemoved, chunkBytes + fileBytes);

        return new ChunkCleanupResult(false, chunksRemoved, filesRemoved, chunkBytes + fileBytes);
    }

    private async Task<(int Count, long Bytes)> RemoveUnusedChunksAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var count = 0;
        var bytes = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var unused = await context.Set<Chunk>()
                .Where(c => c.CreatedOn <= cutoff && !c.Archives!.Any())
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (unused.Count == 0)
                break;

            foreach (var chunk in unused)
            {
                // The file first: a record without its file would be served as a chunk that 404s
                if (await chunkStore.DeleteAsync(chunk.Hash))
                    bytes += chunk.StoredSize;
            }

            context.Set<Chunk>().RemoveRange(unused);

            await context.SaveChangesAsync(cancellationToken);

            count += unused.Count;
        }

        return (count, bytes);
    }

    private async Task<(int Count, long Bytes)> RemoveLeftoverFilesAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var count = 0;
        var bytes = 0L;

        var old = (await chunkStore.EnumerateFilesAsync()).Where(f => f.LastWriteTimeUtc <= cutoff);

        foreach (var batch in old.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var named = batch.Where(f => f.Hash != null).Select(f => f.Hash!).Distinct().ToList();

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var recorded = (await context.Set<Chunk>()
                    .Where(c => named.Contains(c.Hash))
                    .Select(c => c.Hash)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var file in batch)
            {
                // A chunk's file that has its record stays; so does anything not written by the store
                var leftover = file.Hash != null
                    ? !recorded.Contains(file.Hash)
                    : file.Path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

                if (!leftover)
                    continue;

                try
                {
                    var length = new FileInfo(file.Path).Length;

                    File.Delete(file.Path);

                    count++;
                    bytes += length;
                }
                catch (IOException ex)
                {
                    logger.LogDebug(ex, "Could not remove leftover chunk store file {Path}", file.Path);
                }
            }
        }

        return (count, bytes);
    }
}

using LANCommander.SDK;
using LANCommander.SDK.Chunking;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data.Models;
using Microsoft.Extensions.Logging;

namespace LANCommander.Server.Services.Chunks;

/// <summary>A chunk as it sits in the store.</summary>
/// <param name="Hash">The SHA-256 of the chunk's uncompressed bytes.</param>
/// <param name="Path">The file holding it.</param>
/// <param name="StoredSize">The file's size: the compressed size when <paramref name="Compression"/> isn't None.</param>
/// <param name="Compression">How the file's bytes are stored.</param>
public sealed record StoredChunk(string Hash, string Path, long StoredSize, ChunkCompression Compression);

/// <summary>A file in the chunk store.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="Hash">The chunk it's named for, or null for a file named for none (a temporary file).</param>
/// <param name="LastWriteTimeUtc">When it was last written.</param>
public sealed record StoreFile(string Path, string? Hash, DateTime LastWriteTimeUtc);

/// <summary>
/// The server's store of chunks: one file per chunk, named by its hash, under the default
/// <see cref="StorageLocationType.Chunk"/> storage location. Chunks are spread over two levels of
/// folders by the start of their hash (<c>ab/cd/abcd…</c>) and compressed only when that saves space;
/// a compressed chunk's file ends in <c>.deflate</c>. Writing a chunk that's already stored does nothing,
/// so any number of archives can share one.
/// </summary>
public sealed class ChunkStore(StorageLocationService storageLocationService, ILogger<ChunkStore> logger)
{
    /// <summary>Where the store lives unless an administrator chooses otherwise, relative to the data directory.</summary>
    public const string DefaultPath = "Chunks";

    public const string CompressedExtension = ".deflate";

    /// <summary>A chunk is kept compressed only if that makes it at least this much smaller.</summary>
    public const double MinimumSaving = 0.05;

    private StorageLocation? _storageLocation;

    /// <summary>The default chunk storage location, made the first time it's needed.</summary>
    public async Task<StorageLocation> GetStorageLocationAsync()
    {
        if (_storageLocation != null)
            return _storageLocation;

        var existing = await storageLocationService.DefaultAsync(StorageLocationType.Chunk);

        if (existing == null)
        {
            var added = await storageLocationService.AddMissingAsync(
                l => l.Type == StorageLocationType.Chunk && l.Default,
                new StorageLocation
                {
                    // Relative to the data directory, like the other defaults
                    Path = DefaultPath,
                    Type = StorageLocationType.Chunk,
                    Default = true,
                });

            existing = added.Value;
        }

        return _storageLocation = existing;
    }

    /// <summary>The file a chunk is stored in, compressed or not.</summary>
    public static string GetFilePath(StorageLocation storageLocation, string hash, ChunkCompression compression)
    {
        EnsureValid(hash);

        var name = compression == ChunkCompression.None ? hash : hash + CompressedExtension;

        return AppPaths.ResolveStorageLocationPath(storageLocation.Path, hash[..2], hash[2..4], name);
    }

    /// <summary>
    /// Every file in the store, with the hash it's named for, or null for a file named for no chunk,
    /// such as a temporary file a crash left behind.
    /// </summary>
    public async Task<IEnumerable<StoreFile>> EnumerateFilesAsync()
    {
        var root = AppPaths.ResolveStorageLocationPath((await GetStorageLocationAsync()).Path);

        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path =>
        {
            var name = System.IO.Path.GetFileName(path);
            var hash = name.EndsWith(CompressedExtension, StringComparison.Ordinal) ? name[..^CompressedExtension.Length] : name;

            return new StoreFile(path, ChunkHash.IsValid(hash) ? hash : null, File.GetLastWriteTimeUtc(path));
        });
    }

    /// <summary>The stored chunk, or null if the store doesn't have it.</summary>
    public async Task<StoredChunk?> FindAsync(string hash)
    {
        EnsureValid(hash);

        var storageLocation = await GetStorageLocationAsync();

        foreach (var compression in new[] { ChunkCompression.None, ChunkCompression.Deflate })
        {
            var path = GetFilePath(storageLocation, hash, compression);
            var file = new FileInfo(path);

            if (file.Exists)
                return new StoredChunk(hash, path, file.Length, compression);
        }

        return null;
    }

    public async Task<bool> ExistsAsync(string hash) => await FindAsync(hash) != null;

    /// <summary>
    /// Stores a chunk unless it's already stored. <paramref name="data"/> must hash to
    /// <paramref name="hash"/>, so a bad caller can't put wrong bytes under a chunk's name.
    /// </summary>
    public async Task<StoredChunk> WriteAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureValid(hash);

        if (ChunkHash.Compute(data.Span) != hash)
            throw new ArgumentException($"The data doesn't hash to {hash}", nameof(data));

        var existing = await FindAsync(hash);

        if (existing != null)
            return existing;

        var compressed = ChunkCodec.Compress(data.Span);

        var (bytes, compression) = compressed.Length <= data.Length * (1 - MinimumSaving)
            ? (new ReadOnlyMemory<byte>(compressed), ChunkCompression.Deflate)
            : (data, ChunkCompression.None);

        var path = GetFilePath(await GetStorageLocationAsync(), hash, compression);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // Written beside its final name, then renamed, so nobody ever reads half a chunk
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await stream.WriteAsync(bytes, cancellationToken);

            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer stored the same chunk first; its bytes are the same
            logger.LogDebug("Chunk {Hash} was stored by another writer", hash);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return await FindAsync(hash) ?? throw new IOException($"Chunk {hash} could not be stored at {path}");
    }

    /// <summary>The chunk's stored bytes and how they're stored, or null if it isn't stored.</summary>
    public async Task<(Stream Stream, StoredChunk Chunk)?> OpenStoredAsync(string hash)
    {
        var chunk = await FindAsync(hash);

        if (chunk == null)
            return null;

        Stream stream = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        return (stream, chunk);
    }

    /// <summary>The chunk's uncompressed bytes, or null if it isn't stored.</summary>
    public async Task<byte[]?> ReadAsync(string hash, CancellationToken cancellationToken = default)
    {
        var opened = await OpenStoredAsync(hash);

        if (opened == null)
            return null;

        var (stored, chunk) = opened.Value;

        await using (stored)
        await using (var decompressed = ChunkCodec.OpenDecompressed(stored, chunk.Compression))
        using (var output = new MemoryStream())
        {
            await decompressed.CopyToAsync(output, cancellationToken);

            return output.ToArray();
        }
    }

    /// <summary>Removes a chunk; false if it wasn't stored.</summary>
    public async Task<bool> DeleteAsync(string hash)
    {
        var chunk = await FindAsync(hash);

        if (chunk == null)
            return false;

        File.Delete(chunk.Path);

        return true;
    }

    // The hash is part of a file path, so anything but a hash is refused before it gets near one
    private static void EnsureValid(string hash)
    {
        if (!ChunkHash.IsValid(hash))
            throw new ArgumentException("A chunk hash is 64 lowercase hex characters", nameof(hash));
    }
}

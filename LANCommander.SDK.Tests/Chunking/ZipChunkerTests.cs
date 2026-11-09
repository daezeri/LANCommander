using System.IO.Compression;
using LANCommander.SDK.Chunking;

namespace LANCommander.SDK.Tests.Chunking;

/// <summary>
/// A zip archive converts to a manifest whose chunks rebuild exactly the files the zip would extract.
/// </summary>
public class ZipChunkerTests
{
    private const int ChunkSize = 64;

    private readonly Dictionary<string, byte[]> _store = new();

    private Task Store(ChunkData chunk, CancellationToken _)
    {
        _store[chunk.Hash] = chunk.Data.ToArray();

        return Task.CompletedTask;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];

        new Random(seed).NextBytes(data);

        return data;
    }

    private static ZipArchive Zip(Action<ZipArchive> fill)
    {
        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            fill(zip);

        stream.Position = 0;

        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static void AddFile(ZipArchive zip, string path, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(path, level);

        using var stream = entry.Open();

        stream.Write(data);
    }

    [Fact]
    public async Task Zip_ConvertsToManifestThatRebuildsEveryFile()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["game.exe"] = Bytes(1000, 1),
            ["maps/de_dust.bsp"] = Bytes(ChunkSize * 3, 2),
            ["readme.txt"] = "Hello"u8.ToArray(),
            ["empty.cfg"] = [],
        };

        using var zip = Zip(z =>
        {
            z.CreateEntry("maps/");
            z.CreateEntry("saves/");

            foreach (var (path, data) in files)
                AddFile(z, path, data, path == "game.exe" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        });

        var manifest = await ZipChunker.BuildManifestAsync(zip, Store, ChunkSize);

        Assert.Equal(["maps/", "saves/"], manifest.Directories);
        Assert.Equal(files.Keys.OrderBy(k => k), manifest.Files.Select(f => f.Path).OrderBy(p => p));

        foreach (var file in manifest.Files)
        {
            var rebuilt = file.Chunks.SelectMany(hash => _store[hash]).ToArray();

            Assert.Equal(files[file.Path], rebuilt);
            Assert.Equal(files[file.Path].Length, file.Size);
            Assert.Equal(Force.Crc32.Crc32Algorithm.Compute(files[file.Path]), file.Crc32);
            Assert.NotNull(file.Modified);
        }
    }

    [Fact]
    public async Task UnchangedFilesInANewVersion_KeepTheirChunks()
    {
        var shared = Bytes(ChunkSize * 4, 3);

        using var v1 = Zip(z =>
        {
            AddFile(z, "data.pak", shared);
            AddFile(z, "game.exe", Bytes(100, 4));
        });

        using var v2 = Zip(z =>
        {
            AddFile(z, "data.pak", shared);
            AddFile(z, "game.exe", Bytes(100, 5));
        });

        var first = await ZipChunker.BuildManifestAsync(v1, Store, ChunkSize);
        var second = await ZipChunker.BuildManifestAsync(v2, Store, ChunkSize);

        Assert.Equal(
            first.Files.Single(f => f.Path == "data.pak").Chunks,
            second.Files.Single(f => f.Path == "data.pak").Chunks);

        Assert.NotEqual(
            first.Files.Single(f => f.Path == "game.exe").Chunks,
            second.Files.Single(f => f.Path == "game.exe").Chunks);
    }

    [Fact]
    public async Task ZipWithAPathEscapingTheInstall_IsRejected()
    {
        using var zip = Zip(z => AddFile(z, "../evil.dll", [1, 2, 3]));

        await Assert.ThrowsAsync<ChunkManifestException>(() => ZipChunker.BuildManifestAsync(zip, Store, ChunkSize));
    }

    [Fact]
    public async Task Cancellation_StopsTheConversion()
    {
        using var zip = Zip(z => AddFile(z, "a.bin", Bytes(10, 6)));
        using var cts = new CancellationTokenSource();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ZipChunker.BuildManifestAsync(zip, Store, ChunkSize, cts.Token));
    }
}

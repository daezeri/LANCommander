using System.Security.Cryptography;
using System.Text;
using LANCommander.SDK.Chunking;

namespace LANCommander.SDK.Tests.Chunking;

/// <summary>
/// The builder cuts files into fixed-size chunks named by their SHA-256, records each file's size, CRC
/// and chunk list, and refuses paths that could escape the install directory.
/// </summary>
public class ChunkManifestBuilderTests
{
    private const int ChunkSize = 16;

    private readonly Dictionary<string, byte[]> _stored = new();
    private readonly List<string> _handedOver = new();

    private Task Store(ChunkData chunk, CancellationToken _)
    {
        _handedOver.Add(chunk.Hash);
        _stored[chunk.Hash] = chunk.Data.ToArray();

        return Task.CompletedTask;
    }

    private static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];

        new Random(seed).NextBytes(data);

        return data;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private Task<ChunkManifestFile> AddAsync(ChunkManifestBuilder builder, string path, byte[] data, uint? crc = null) =>
        builder.AddFileAsync(path, null, new MemoryStream(data), crc, Store);

    [Fact]
    public async Task EmptyFile_HasNoChunks()
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await AddAsync(builder, "empty.txt", []);

        Assert.Equal(0, file.Size);
        Assert.Empty(file.Chunks);
        Assert.Equal(0u, file.Crc32);
        Assert.Empty(_handedOver);
    }

    [Fact]
    public async Task SmallFile_IsOneChunkNamedByItsHash()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await AddAsync(builder, "hello.txt", data);

        Assert.Equal(5, file.Size);
        Assert.Equal([Sha256(data)], file.Chunks);
        Assert.Equal(data, _stored[file.Chunks[0]]);
    }

    [Theory]
    [InlineData(ChunkSize, 1)]
    [InlineData(ChunkSize * 2, 2)]
    [InlineData(ChunkSize * 2 + 5, 3)]
    public async Task LargeFile_IsCutIntoFixedSizeChunks(int length, int expectedChunks)
    {
        var data = Bytes(length);
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await AddAsync(builder, "data.bin", data);

        Assert.Equal(expectedChunks, file.Chunks.Count);

        for (var i = 0; i < file.Chunks.Count; i++)
        {
            var expected = data.Skip(i * ChunkSize).Take(ChunkSize).ToArray();

            Assert.Equal(expected.Length, file.GetChunkLength(i, ChunkSize));
            Assert.Equal(Sha256(expected), file.Chunks[i]);
            Assert.Equal(expected, _stored[file.Chunks[i]]);
        }
    }

    [Fact]
    public async Task ChunksReassembleIntoTheOriginalFile()
    {
        var data = Bytes(ChunkSize * 5 + 3);
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await AddAsync(builder, "data.bin", data);

        var rebuilt = file.Chunks.SelectMany(hash => _stored[hash]).ToArray();

        Assert.Equal(data, rebuilt);
    }

    [Fact]
    public async Task StreamsThatReturnLessThanAskedStillGiveFullChunks()
    {
        var data = Bytes(ChunkSize * 3 + 7);
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await builder.AddFileAsync("trickle.bin", null, new TrickleStream(data), null, Store);

        Assert.Equal(4, file.Chunks.Count);
        Assert.Equal(Sha256(data.Take(ChunkSize).ToArray()), file.Chunks[0]);
    }

    [Fact]
    public async Task RepeatedChunk_IsHandedOverOnce()
    {
        var block = Bytes(ChunkSize, seed: 7);
        var builder = new ChunkManifestBuilder(ChunkSize);

        var first = await AddAsync(builder, "a.bin", [.. block, .. block]);
        var second = await AddAsync(builder, "b.bin", block);

        Assert.Equal(first.Chunks[0], first.Chunks[1]);
        Assert.Equal(first.Chunks[0], second.Chunks[0]);
        Assert.Single(_handedOver);
        Assert.Single(builder.Build().GetDistinctChunks());
    }

    [Fact]
    public async Task Crc_IsRecordedAndChecked()
    {
        var data = Bytes(40);
        var crc = Force.Crc32.Crc32Algorithm.Compute(data);
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await AddAsync(builder, "ok.bin", data, crc);

        Assert.Equal(crc, file.Crc32);

        await Assert.ThrowsAsync<ChunkManifestException>(() => AddAsync(builder, "damaged.bin", data, crc ^ 1));
    }

    [Fact]
    public async Task Paths_AreNormalizedToForwardSlashes()
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        var file = await AddAsync(builder, "maps\\de_dust.bsp", [1]);
        builder.AddDirectory("sound\\");

        Assert.Equal("maps/de_dust.bsp", file.Path);
        Assert.Equal(["sound/"], builder.Build().Directories);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("maps/../../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("game.exe:stream")]
    [InlineData("maps//de_dust.bsp")]
    [InlineData("./game.exe")]
    [InlineData("")]
    public async Task UnsafePaths_AreRejected(string path)
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        await Assert.ThrowsAsync<ChunkManifestException>(() => AddAsync(builder, path, [1]));
    }

    [Fact]
    public async Task DuplicatePaths_AreRejectedRegardlessOfCase()
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        await AddAsync(builder, "Game.exe", [1]);

        await Assert.ThrowsAsync<ChunkManifestException>(() => AddAsync(builder, "game.EXE", [2]));
    }

    [Fact]
    public async Task FileAndDirectoryWithTheSameName_AreRejected()
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        builder.AddDirectory("data/");

        await Assert.ThrowsAsync<ChunkManifestException>(() => AddAsync(builder, "data", [1]));

        await AddAsync(builder, "other", [1]);

        Assert.Throws<ChunkManifestException>(() => builder.AddDirectory("other/"));
    }

    [Fact]
    public void RepeatedDirectory_IsRecordedOnce()
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        builder.AddDirectory("maps/");
        builder.AddDirectory("maps");

        Assert.Equal(["maps/"], builder.Build().Directories);
    }

    [Fact]
    public void ChunkSize_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkManifestBuilder(0));
    }

    [Fact]
    public async Task Manifest_TotalsTheSizeOfEveryFile()
    {
        var builder = new ChunkManifestBuilder(ChunkSize);

        await AddAsync(builder, "a.bin", Bytes(10));
        await AddAsync(builder, "b.bin", Bytes(30, seed: 2));

        Assert.Equal(40, builder.Build().TotalSize);
    }

    /// <summary>Returns at most three bytes per read, like a decompressing stream might.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}

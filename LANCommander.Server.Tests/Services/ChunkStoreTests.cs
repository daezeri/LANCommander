using LANCommander.SDK.Chunking;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using Shouldly;

namespace LANCommander.Server.Tests.Services;

/// <summary>
/// The chunk store keeps one file per chunk, named by its hash, compressed only when that helps, and
/// never stores bytes under a name they don't hash to.
/// </summary>
[Collection("Application")]
public class ChunkStoreTests(ApplicationFixture fixture) : BaseTest(fixture)
{
    private string StorePath = string.Empty;

    private async Task<ChunkStore> GetStoreAsync()
    {
        StorePath = await EnsureChunkStorageLocationAsync();

        return GetService<ChunkStore>();
    }

    private static byte[] Random(int length)
    {
        var data = new byte[length];

        System.Random.Shared.NextBytes(data);

        return data;
    }

    private static byte[] Compressible(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 7)).ToArray();

    [Fact]
    public async Task WrittenChunk_ReadsBackUnchanged()
    {
        var store = await GetStoreAsync();
        var data = Random(5000);
        var hash = ChunkHash.Compute(data);

        await store.WriteAsync(hash, data);

        (await store.ReadAsync(hash)).ShouldBe(data);
        (await store.ExistsAsync(hash)).ShouldBeTrue();
    }

    [Fact]
    public async Task CompressibleChunk_IsStoredCompressed()
    {
        var store = await GetStoreAsync();
        var data = Compressible(64 * 1024);
        var hash = ChunkHash.Compute(data);

        var stored = await store.WriteAsync(hash, data);

        stored.Compression.ShouldBe(ChunkCompression.Deflate);
        stored.StoredSize.ShouldBeLessThan(data.Length);
        stored.Path.ShouldEndWith(hash + ChunkStore.CompressedExtension);
        (await store.ReadAsync(hash)).ShouldBe(data);
    }

    [Fact]
    public async Task IncompressibleChunk_IsStoredAsIs()
    {
        var store = await GetStoreAsync();
        var data = Random(64 * 1024);
        var hash = ChunkHash.Compute(data);

        var stored = await store.WriteAsync(hash, data);

        stored.Compression.ShouldBe(ChunkCompression.None);
        stored.StoredSize.ShouldBe(data.Length);
        stored.Path.ShouldEndWith(hash);
    }

    [Fact]
    public async Task Chunks_AreSpreadOverFoldersByHashPrefix()
    {
        var store = await GetStoreAsync();
        var data = Random(100);
        var hash = ChunkHash.Compute(data);

        var stored = await store.WriteAsync(hash, data);

        var expected = Path.Combine(StorePath, hash[..2], hash[2..4], hash);

        Path.GetFullPath(stored.Path).ShouldBe(Path.GetFullPath(expected));
    }

    [Fact]
    public async Task WritingAStoredChunkAgain_KeepsTheOneFile()
    {
        var store = await GetStoreAsync();
        var data = Random(1000);
        var hash = ChunkHash.Compute(data);

        var first = await store.WriteAsync(hash, data);
        var writtenAt = File.GetLastWriteTimeUtc(first.Path);

        var second = await store.WriteAsync(hash, data);

        second.Path.ShouldBe(first.Path);
        File.GetLastWriteTimeUtc(second.Path).ShouldBe(writtenAt);
        Directory.GetFiles(Path.GetDirectoryName(first.Path)!, hash + "*").Length.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentWritesOfOneChunk_AllSucceedWithOneFile()
    {
        var store = await GetStoreAsync();
        var data = Random(200_000);
        var hash = ChunkHash.Compute(data);

        // Warm the storage location so the writers race on the file, not on the database
        await store.GetStorageLocationAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.WriteAsync(hash, data))));

        results.Select(r => r.Path).Distinct().Count().ShouldBe(1);
        Directory.GetFiles(Path.GetDirectoryName(results[0].Path)!).Where(f => f.Contains(hash)).Count().ShouldBe(1);
        (await store.ReadAsync(hash)).ShouldBe(data);
    }

    [Fact]
    public async Task DataThatDoesNotMatchItsHash_IsRefused()
    {
        var store = await GetStoreAsync();
        var hash = ChunkHash.Compute(Random(10));

        await Should.ThrowAsync<ArgumentException>(() => store.WriteAsync(hash, Random(10)));
        (await store.ExistsAsync(hash)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("ABCDEF")]
    [InlineData("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824")]
    public async Task AnythingButAHash_IsRefused(string hash)
    {
        var store = await GetStoreAsync();

        await Should.ThrowAsync<ArgumentException>(() => store.FindAsync(hash));
        await Should.ThrowAsync<ArgumentException>(() => store.WriteAsync(hash, new byte[] { 1 }));
    }

    [Fact]
    public async Task DeletedChunk_IsGone()
    {
        var store = await GetStoreAsync();
        var data = Random(300);
        var hash = ChunkHash.Compute(data);

        await store.WriteAsync(hash, data);

        (await store.DeleteAsync(hash)).ShouldBeTrue();
        (await store.ExistsAsync(hash)).ShouldBeFalse();
        (await store.ReadAsync(hash)).ShouldBeNull();
        (await store.DeleteAsync(hash)).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenStored_GivesTheStoredBytesAndTheirCompression()
    {
        var store = await GetStoreAsync();
        var data = Compressible(10_000);
        var hash = ChunkHash.Compute(data);

        await store.WriteAsync(hash, data);

        var opened = await store.OpenStoredAsync(hash);

        opened.ShouldNotBeNull();

        await using var stream = opened.Value.Stream;

        opened.Value.Chunk.Compression.ShouldBe(ChunkCompression.Deflate);
        stream.Length.ShouldBe(opened.Value.Chunk.StoredSize);
    }
}

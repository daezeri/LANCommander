using System.IO.Compression;
using LANCommander.SDK.Chunking;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LANCommander.Server.Tests.Services;

/// <summary>
/// Converting an archive stores its chunks and manifest, records and links the chunks, and marks the
/// archive Ready; a damaged or unsafe archive is marked Failed with the reason instead.
/// </summary>
[Collection("Application")]
public class ChunkManifestServiceTests(ApplicationFixture fixture) : BaseTest(fixture)
{
    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];

        new Random(seed).NextBytes(data);

        return data;
    }

    /// <summary>An archive whose zip, written by <paramref name="fill"/>, is in a storage location of its own.</summary>
    private async Task<Archive> AddArchiveAsync(Action<ZipArchive> fill)
    {
        await EnsureChunkStorageLocationAsync();

        var storageLocation = await GetService<StorageLocationService>().AddAsync(new StorageLocation
        {
            Path = GetTemporaryDirectory(),
            Type = StorageLocationType.Archive,
            Default = false,
        });

        var archiveService = GetService<ArchiveService>();

        var archive = await archiveService.AddAsync(new Archive
        {
            ObjectKey = Guid.NewGuid().ToString(),
            Version = "1.0",
            StorageLocationId = storageLocation.Id,
        });

        var path = await archiveService.GetArchiveFileLocationAsync(archive);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            fill(zip);

        return archive;
    }

    private static void AddFile(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name).Open();

        stream.Write(data);
    }

    private async Task<Archive> ReloadAsync(Guid archiveId)
    {
        await using var context = await GetService<IDbContextFactory<DatabaseContext>>().CreateDbContextAsync();

        return await context.Set<Archive>().AsNoTracking().FirstAsync(a => a.Id == archiveId);
    }

    private async Task<List<string>> LinkedChunksAsync(Guid archiveId)
    {
        await using var context = await GetService<IDbContextFactory<DatabaseContext>>().CreateDbContextAsync();

        return await context.Set<ArchiveChunk>().Where(ac => ac.ArchiveId == archiveId).Select(ac => ac.ChunkHash).ToListAsync();
    }

    [Fact]
    public async Task Build_StoresChunksAndManifestAndMarksReady()
    {
        var level = Bytes(ChunkManifest.DefaultChunkSize + 1000, 1);

        var archive = await AddArchiveAsync(zip =>
        {
            zip.CreateEntry("saves/");
            AddFile(zip, "data/level.pak", level);
            AddFile(zip, "game.exe", Bytes(500, 2));
        });

        var service = GetService<ChunkManifestService>();

        (await service.BuildAsync(archive.Id)).ShouldBe(ChunkManifestStatus.Ready);

        var reloaded = await ReloadAsync(archive.Id);

        reloaded.ManifestStatus.ShouldBe(ChunkManifestStatus.Ready);
        reloaded.ManifestError.ShouldBeNull();
        reloaded.ManifestBuiltOn.ShouldNotBeNull();
        ChunkHash.IsValid(reloaded.ManifestHash!).ShouldBeTrue();

        var manifest = await service.GetManifestAsync(archive.Id);

        manifest.ShouldNotBeNull();
        manifest.Directories.ShouldBe(["saves/"]);
        manifest.Files.Select(f => f.Path).ShouldBe(["data/level.pak", "game.exe"], ignoreOrder: true);

        // level.pak's two chunks, game.exe's one, and the manifest itself
        var linked = await LinkedChunksAsync(archive.Id);

        linked.Count.ShouldBe(4);
        linked.ShouldContain(reloaded.ManifestHash!);

        // Every chunk reads back, and level.pak rebuilds exactly
        var store = GetService<ChunkStore>();
        var levelFile = manifest.Files.Single(f => f.Path == "data/level.pak");
        var rebuilt = new List<byte>();

        foreach (var hash in levelFile.Chunks)
            rebuilt.AddRange((await store.ReadAsync(hash))!);

        rebuilt.ToArray().ShouldBe(level);
    }

    [Fact]
    public async Task ArchivesSharingFiles_ShareChunkRows()
    {
        var shared = Bytes(3000, 3);

        var first = await AddArchiveAsync(zip => AddFile(zip, "shared.bin", shared));
        var second = await AddArchiveAsync(zip =>
        {
            AddFile(zip, "shared.bin", shared);
            AddFile(zip, "new.bin", Bytes(100, 4));
        });

        var service = GetService<ChunkManifestService>();

        await service.BuildAsync(first.Id);
        await service.BuildAsync(second.Id);

        var sharedHash = ChunkHash.Compute(shared);

        await using var context = await GetService<IDbContextFactory<DatabaseContext>>().CreateDbContextAsync();

        (await context.Set<Chunk>().CountAsync(c => c.Hash == sharedHash)).ShouldBe(1);
        (await context.Set<ArchiveChunk>().CountAsync(ac => ac.ChunkHash == sharedHash)).ShouldBe(2);
    }

    [Fact]
    public async Task Rebuild_ReplacesLinksWithTheNewContents()
    {
        var original = Bytes(100, 5);
        var archive = await AddArchiveAsync(zip => AddFile(zip, "a.bin", original));
        var service = GetService<ChunkManifestService>();

        await service.BuildAsync(archive.Id);

        (await LinkedChunksAsync(archive.Id)).ShouldContain(ChunkHash.Compute(original));

        // The archive is repacked with different contents
        var path = await GetService<ArchiveService>().GetArchiveFileLocationAsync(archive);

        File.Delete(path);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            AddFile(zip, "b.bin", Bytes(100, 6));

        await service.BuildAsync(archive.Id);

        var after = await LinkedChunksAsync(archive.Id);

        after.Count.ShouldBe(2);
        after.ShouldNotContain(ChunkHash.Compute(original));
        (await service.GetManifestAsync(archive.Id))!.Files.Single().Path.ShouldBe("b.bin");
    }

    [Fact]
    public async Task Rebuild_OfAnUnchangedArchive_KeepsTheSameManifest()
    {
        var archive = await AddArchiveAsync(zip => AddFile(zip, "a.bin", Bytes(100, 7)));
        var service = GetService<ChunkManifestService>();

        await service.BuildAsync(archive.Id);
        var first = (await ReloadAsync(archive.Id)).ManifestHash;

        await service.BuildAsync(archive.Id);
        var second = (await ReloadAsync(archive.Id)).ManifestHash;

        second.ShouldBe(first);
        (await LinkedChunksAsync(archive.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ArchiveWithAnUnsafePath_IsMarkedFailed()
    {
        var archive = await AddArchiveAsync(zip => AddFile(zip, "../evil.dll", [1, 2, 3]));

        (await GetService<ChunkManifestService>().BuildAsync(archive.Id)).ShouldBe(ChunkManifestStatus.Failed);

        var reloaded = await ReloadAsync(archive.Id);

        reloaded.ManifestStatus.ShouldBe(ChunkManifestStatus.Failed);
        reloaded.ManifestError.ShouldNotBeNullOrWhiteSpace();
        reloaded.ManifestHash.ShouldBeNull();
        (await LinkedChunksAsync(archive.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingArchiveFile_IsMarkedFailed()
    {
        var archive = await AddArchiveAsync(zip => AddFile(zip, "a.bin", [1]));

        File.Delete(await GetService<ArchiveService>().GetArchiveFileLocationAsync(archive));

        (await GetService<ChunkManifestService>().BuildAsync(archive.Id)).ShouldBe(ChunkManifestStatus.Failed);
        (await ReloadAsync(archive.Id)).ManifestError!.ShouldContain("missing");
    }

    [Fact]
    public async Task FileThatIsNotAZip_IsMarkedFailed()
    {
        var archive = await AddArchiveAsync(zip => AddFile(zip, "a.bin", [1]));

        await File.WriteAllTextAsync(await GetService<ArchiveService>().GetArchiveFileLocationAsync(archive), "not a zip");

        (await GetService<ChunkManifestService>().BuildAsync(archive.Id)).ShouldBe(ChunkManifestStatus.Failed);
    }

    [Fact]
    public async Task MarkPending_ShowsTheArchiveAsQueued()
    {
        var archive = await AddArchiveAsync(zip => AddFile(zip, "a.bin", [1]));

        await GetService<ChunkManifestService>().MarkPendingAsync(archive.Id);

        (await ReloadAsync(archive.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Pending);
    }

    [Fact]
    public async Task UnconvertedArchive_HasNoManifest()
    {
        var archive = await AddArchiveAsync(zip => AddFile(zip, "a.bin", [1]));

        (await GetService<ChunkManifestService>().GetManifestAsync(archive.Id)).ShouldBeNull();
    }
}

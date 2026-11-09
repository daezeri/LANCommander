using System.IO.Compression;
using LANCommander.SDK.Chunking;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LANCommander.Server.Tests.Services;

/// <summary>
/// Cleanup removes chunks no archive links to and files a conversion never recorded, keeps whatever
/// is still used or newer than the grace period, and never runs during a conversion.
/// </summary>
[Collection("Application")]
public class ChunkStoreCleanerTests(ApplicationFixture fixture) : BaseTest(fixture)
{
    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];

        new Random(seed).NextBytes(data);

        return data;
    }

    private async Task<Archive> AddConvertedArchiveAsync(params (string Name, byte[] Contents)[] files)
    {
        await EnsureChunkStorageLocationAsync();

        var storageLocation = await GetService<StorageLocationService>().AddAsync(new StorageLocation
        {
            Path = GetTemporaryDirectory(),
            Type = StorageLocationType.Archive,
        });

        var archiveService = GetService<ArchiveService>();

        var archive = await archiveService.AddAsync(new Archive
        {
            ObjectKey = Guid.NewGuid().ToString(),
            Version = "1.0",
            StorageLocationId = storageLocation.Id,
        });

        using (var zip = ZipFile.Open(await archiveService.GetArchiveFileLocationAsync(archive), ZipArchiveMode.Create))
        {
            foreach (var (name, contents) in files)
            {
                using var stream = zip.CreateEntry(name).Open();

                stream.Write(contents);
            }
        }

        await GetService<ChunkManifestService>().BuildAsync(archive.Id);

        return await archiveService.AsNoTracking().GetAsync(archive.Id);
    }

    private async Task<bool> RecordExistsAsync(string hash)
    {
        await using var context = await GetService<IDbContextFactory<DatabaseContext>>().CreateDbContextAsync();

        return await context.Set<Chunk>().AnyAsync(c => c.Hash == hash);
    }

    private ChunkStoreCleaner Cleaner => GetService<ChunkStoreCleaner>();

    [Fact]
    public async Task ChunksOfADeletedArchive_AreRemoved_ButSharedOnesStay()
    {
        var shared = Bytes(400, 1);
        var onlyInDeleted = Bytes(400, 2);

        var deleted = await AddConvertedArchiveAsync(("shared.bin", shared), ("mine.bin", onlyInDeleted));
        var kept = await AddConvertedArchiveAsync(("shared.bin", shared));

        var archiveService = GetService<ArchiveService>();
        await archiveService.DeleteAsync(await archiveService.GetAsync(deleted.Id));

        var result = await Cleaner.CleanUpAsync(TimeSpan.Zero);

        result.Skipped.ShouldBeFalse();
        result.UnusedChunksRemoved.ShouldBeGreaterThanOrEqualTo(2); // its own file and its manifest

        var store = GetService<ChunkStore>();

        (await store.ExistsAsync(ChunkHash.Compute(onlyInDeleted))).ShouldBeFalse();
        (await RecordExistsAsync(ChunkHash.Compute(onlyInDeleted))).ShouldBeFalse();
        (await store.ExistsAsync(deleted.ManifestHash!)).ShouldBeFalse();

        (await store.ExistsAsync(ChunkHash.Compute(shared))).ShouldBeTrue();
        (await store.ExistsAsync(kept.ManifestHash!)).ShouldBeTrue();
    }

    [Fact]
    public async Task UnusedChunks_NewerThanTheGracePeriod_Stay()
    {
        var contents = Bytes(300, 3);
        var archive = await AddConvertedArchiveAsync(("a.bin", contents));

        var archiveService = GetService<ArchiveService>();
        await archiveService.DeleteAsync(await archiveService.GetAsync(archive.Id));

        await Cleaner.CleanUpAsync(TimeSpan.FromHours(1));

        (await GetService<ChunkStore>().ExistsAsync(ChunkHash.Compute(contents))).ShouldBeTrue();
        (await RecordExistsAsync(ChunkHash.Compute(contents))).ShouldBeTrue();
    }

    [Fact]
    public async Task FilesNoChunkRecordNames_AreRemoved_OnceOldEnough()
    {
        await EnsureChunkStorageLocationAsync();

        var store = GetService<ChunkStore>();

        // Written by a conversion that crashed before recording anything
        var oldData = Bytes(200, 4);
        var oldFile = await store.WriteAsync(ChunkHash.Compute(oldData), oldData);
        File.SetLastWriteTimeUtc(oldFile.Path, DateTime.UtcNow.AddDays(-2));

        var newData = Bytes(200, 5);
        var newFile = await store.WriteAsync(ChunkHash.Compute(newData), newData);

        var temporary = oldFile.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, [1, 2, 3]);
        File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddDays(-2));

        var result = await Cleaner.CleanUpAsync(TimeSpan.FromDays(1));

        result.LeftoverFilesRemoved.ShouldBeGreaterThanOrEqualTo(2);
        File.Exists(oldFile.Path).ShouldBeFalse();
        File.Exists(temporary).ShouldBeFalse();
        File.Exists(newFile.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task ChunksStillLinked_AreNeverRemoved()
    {
        var contents = Bytes(500, 6);
        var archive = await AddConvertedArchiveAsync(("a.bin", contents));

        await Cleaner.CleanUpAsync(TimeSpan.Zero);

        var store = GetService<ChunkStore>();

        (await store.ExistsAsync(ChunkHash.Compute(contents))).ShouldBeTrue();
        (await store.ExistsAsync(archive.ManifestHash!)).ShouldBeTrue();
        (await GetService<ChunkManifestService>().GetManifestAsync(archive.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cleanup_DoesNotRunDuringAConversion()
    {
        await EnsureChunkStorageLocationAsync();

        using (await GetService<ChunkStoreGate>().EnterConversionAsync())
        {
            (await Cleaner.CleanUpAsync(TimeSpan.Zero)).Skipped.ShouldBeTrue();
        }

        (await Cleaner.CleanUpAsync(TimeSpan.Zero)).Skipped.ShouldBeFalse();
    }

    [Fact]
    public async Task ConversionsWait_WhileCleanupRuns()
    {
        var gate = GetService<ChunkStoreGate>();

        var cleanup = await gate.TryEnterCleanupAsync();

        cleanup.ShouldNotBeNull();

        var conversion = gate.EnterConversionAsync();

        await Task.Delay(100);
        conversion.IsCompleted.ShouldBeFalse();

        cleanup.Dispose();

        using (await conversion.WaitAsync(TimeSpan.FromSeconds(5)))
            gate.ConversionRunning.ShouldBeTrue();

        gate.ConversionRunning.ShouldBeFalse();
    }
}

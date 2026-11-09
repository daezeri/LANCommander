using System.IO.Compression;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Jobs.Recurring;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace LANCommander.Server.Tests.Services;

/// <summary>
/// The sweep converts archives waiting for it (never converted, or out of date), leaves converted
/// and failed ones alone, and puts back archives a restart left converting.
/// </summary>
[Collection("Application")]
public class ChunkManifestSweepTests(ApplicationFixture fixture) : BaseTest(fixture)
{
    private async Task<Archive> AddArchiveAsync(byte[] contents, ChunkManifestStatus status = ChunkManifestStatus.None, string? manifestHash = null)
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
            UncompressedSize = contents.Length,
        });

        using (var zip = ZipFile.Open(await archiveService.GetArchiveFileLocationAsync(archive), ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("a.bin").Open())
            stream.Write(contents);

        if (status != ChunkManifestStatus.None)
            await SetStatusAsync(archive.Id, status, manifestHash);

        return archive;
    }

    private async Task SetStatusAsync(Guid archiveId, ChunkManifestStatus status, string? manifestHash)
    {
        await using var context = await GetService<IDbContextFactory<DatabaseContext>>().CreateDbContextAsync();

        var archive = await context.Set<Archive>().FirstAsync(a => a.Id == archiveId);

        archive.ManifestStatus = status;
        archive.ManifestHash = manifestHash;

        await context.SaveChangesAsync();
    }

    private Task<Archive> ReloadAsync(Guid id) => GetService<ArchiveService>().AsNoTracking().GetAsync(id);

    private ChunkManifestSweep CreateSweep() => new(
        GetService<ChunkManifestService>(),
        GetService<SettingsProvider<Settings.Settings>>(),
        NullLogger<ChunkManifestSweep>.Instance);

    [Fact]
    public async Task WaitingArchives_AreListedOldestFirst_AndOthersAreNot()
    {
        var never = await AddArchiveAsync([1]);
        var stale = await AddArchiveAsync([2], ChunkManifestStatus.Stale, new string('a', 64));
        var failed = await AddArchiveAsync([3], ChunkManifestStatus.Failed);
        var ready = await AddArchiveAsync([4], ChunkManifestStatus.Ready, new string('b', 64));

        var waiting = (await GetService<ChunkManifestService>().GetArchivesToConvertAsync(int.MaxValue)).ToList();

        waiting.ShouldContain(never.Id);
        waiting.ShouldContain(stale.Id);
        waiting.ShouldNotContain(failed.Id);
        waiting.ShouldNotContain(ready.Id);
        waiting.IndexOf(never.Id).ShouldBeLessThan(waiting.IndexOf(stale.Id));
    }

    [Fact]
    public async Task Sweep_ConvertsWaitingArchives_AndLeavesFailedOnes()
    {
        var never = await AddArchiveAsync([5, 5, 5]);
        var stale = await AddArchiveAsync([6, 6, 6], ChunkManifestStatus.Stale, new string('c', 64));
        var failed = await AddArchiveAsync([7], ChunkManifestStatus.Failed);

        (await CreateSweep().ConvertAsync(int.MaxValue)).ShouldBeGreaterThanOrEqualTo(2);

        (await ReloadAsync(never.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Ready);
        (await ReloadAsync(stale.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Ready);
        (await ReloadAsync(failed.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Failed);
    }

    [Fact]
    public async Task Sweep_StopsAtItsLimit()
    {
        await AddArchiveAsync([8]);
        await AddArchiveAsync([9]);

        (await CreateSweep().ConvertAsync(1)).ShouldBe(1);
    }

    [Fact]
    public async Task InterruptedConversions_GoBackToWaiting()
    {
        var neverConverted = await AddArchiveAsync([10], ChunkManifestStatus.Pending);
        var convertedBefore = await AddArchiveAsync([11], ChunkManifestStatus.Pending, new string('d', 64));

        (await GetService<ChunkManifestService>().ResetInterruptedAsync()).ShouldBeGreaterThanOrEqualTo(2);

        (await ReloadAsync(neverConverted.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.None);
        (await ReloadAsync(convertedBefore.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Stale);
    }

    [Fact]
    public async Task Progress_CountsEachStateAndWhatsLeft()
    {
        var service = GetService<ChunkManifestService>();
        var before = await service.GetProgressAsync();

        await AddArchiveAsync(new byte[1000]);
        await AddArchiveAsync(new byte[500], ChunkManifestStatus.Stale, new string('e', 64));
        await AddArchiveAsync(new byte[10], ChunkManifestStatus.Failed);

        var after = await service.GetProgressAsync();

        after.Count(ChunkManifestStatus.None).ShouldBe(before.Count(ChunkManifestStatus.None) + 1);
        after.Count(ChunkManifestStatus.Stale).ShouldBe(before.Count(ChunkManifestStatus.Stale) + 1);
        after.Count(ChunkManifestStatus.Failed).ShouldBe(before.Count(ChunkManifestStatus.Failed) + 1);
        after.Remaining.ShouldBe(before.Remaining + 2);
        after.RemainingUncompressedSize.ShouldBe(before.RemainingUncompressedSize + 1500);
    }

    [Fact]
    public async Task ScheduledSweep_DoesNothingWhileTheSettingIsOff()
    {
        var settings = GetService<SettingsProvider<Settings.Settings>>().CurrentValue.Server.Archives;
        var original = settings.GenerateChunkManifests;

        try
        {
            settings.GenerateChunkManifests = false;

            var archive = await AddArchiveAsync([12]);

            await CreateSweep().ExecuteAsync();

            (await ReloadAsync(archive.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.None);
        }
        finally
        {
            settings.GenerateChunkManifests = original;
        }
    }
}

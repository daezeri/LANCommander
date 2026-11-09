using System.IO.Compression;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LANCommander.SDK.Chunking;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Jobs.Background;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace LANCommander.Server.Tests.Services;

/// <summary>
/// Archives are queued for conversion when Generate Chunk Manifests is on, a converted archive is
/// marked out of date when its zip is rewritten, and a deleted archive's chunk links go with it.
/// </summary>
[Collection("Application")]
public class ChunkManifestTriggerTests(ApplicationFixture fixture) : BaseTest(fixture), IDisposable
{
    private readonly RecordingJobClient _jobs = new();
    private bool? _originalSetting;

    private SettingsProvider<Settings.Settings> SettingsProvider => GetService<SettingsProvider<Settings.Settings>>();

    private void SetGenerate(bool enabled)
    {
        _originalSetting ??= SettingsProvider.CurrentValue.Server.Archives.GenerateChunkManifests;

        SettingsProvider.CurrentValue.Server.Archives.GenerateChunkManifests = enabled;
    }

    void IDisposable.Dispose()
    {
        if (_originalSetting.HasValue)
            SettingsProvider.CurrentValue.Server.Archives.GenerateChunkManifests = _originalSetting.Value;

        Dispose();
    }

    private ChunkManifestQueue CreateQueue() => new(
        GetService<ChunkManifestService>(),
        SettingsProvider,
        _jobs,
        NullLogger<ChunkManifestQueue>.Instance);

    private async Task<Archive> AddArchiveAsync(byte[] contents)
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
        using (var stream = zip.CreateEntry("a.bin").Open())
            stream.Write(contents);

        return archive;
    }

    private Task<Archive> ReloadAsync(Guid id) => GetService<ArchiveService>().AsNoTracking().GetAsync(id);

    [Fact]
    public async Task QueueIfEnabled_DoesNothingWhileTheSettingIsOff()
    {
        SetGenerate(false);

        var archive = await AddArchiveAsync([1]);

        (await CreateQueue().QueueIfEnabledAsync(archive.Id)).ShouldBeFalse();

        _jobs.Created.ShouldBeEmpty();
        (await ReloadAsync(archive.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.None);
    }

    [Fact]
    public async Task QueueIfEnabled_MarksPendingAndQueuesTheBuild()
    {
        SetGenerate(true);

        var archive = await AddArchiveAsync([2]);

        (await CreateQueue().QueueIfEnabledAsync(archive.Id)).ShouldBeTrue();

        var job = _jobs.Created.ShouldHaveSingleItem();

        job.Type.ShouldBe(typeof(BuildChunkManifestBackgroundJob));
        job.Args[0].ShouldBe(archive.Id);
        (await ReloadAsync(archive.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Pending);
    }

    [Fact]
    public async Task Queue_IgnoresTheSetting_ForAnAdministratorsRequest()
    {
        SetGenerate(false);

        var archive = await AddArchiveAsync([3]);

        await CreateQueue().QueueAsync(archive.Id);

        _jobs.Created.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueueIfEnabled_NeverThrows()
    {
        SetGenerate(true);

        // An archive that doesn't exist can't be marked Pending
        (await CreateQueue().QueueIfEnabledAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    [Fact]
    public async Task Repack_MarksAConvertedArchiveOutOfDate()
    {
        var archive = await AddArchiveAsync([4, 5, 6]);

        await GetService<ChunkManifestService>().BuildAsync(archive.Id);

        await GetService<ArchiveService>().RepackArchiveAsync(archive.Id);

        (await ReloadAsync(archive.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.Stale);
    }

    [Fact]
    public async Task Repack_LeavesAnUnconvertedArchiveUnconverted()
    {
        var archive = await AddArchiveAsync([7]);

        await GetService<ArchiveService>().RepackArchiveAsync(archive.Id);

        (await ReloadAsync(archive.Id)).ManifestStatus.ShouldBe(ChunkManifestStatus.None);
    }

    [Fact]
    public async Task Delete_RemovesTheArchivesLinks_ButKeepsTheChunks()
    {
        var contents = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var archive = await AddArchiveAsync(contents);

        await GetService<ChunkManifestService>().BuildAsync(archive.Id);

        var archiveService = GetService<ArchiveService>();

        await archiveService.DeleteAsync(await archiveService.GetAsync(archive.Id));

        await using var context = await GetService<IDbContextFactory<DatabaseContext>>().CreateDbContextAsync();

        (await context.Set<ArchiveChunk>().CountAsync(ac => ac.ArchiveId == archive.Id)).ShouldBe(0);
        (await GetService<ChunkStore>().ExistsAsync(ChunkHash.Compute(contents))).ShouldBeTrue();
    }

    /// <summary>Records the jobs it's asked to create instead of running them.</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];

        public string Create(Job job, IState state)
        {
            Created.Add(job);

            return Created.Count.ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}

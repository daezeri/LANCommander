using System.IO.Compression;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using Shouldly;

namespace LANCommander.Server.Tests.Services;

/// <summary>
/// An archive's manifest fields belong to the chunk manifest service. Saving an archive for any other
/// reason (its version, its sizes) keeps what's stored, even if the copy being saved was loaded
/// before a conversion finished.
/// </summary>
[Collection("Application")]
public class ArchiveManifestFieldsTests(ApplicationFixture fixture) : BaseTest(fixture)
{
    [Fact]
    public async Task SavingAnArchiveLoadedBeforeItsConversion_KeepsTheManifest()
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
            stream.Write([1, 2, 3]);

        // The admin UI loaded the archive while it was unconverted...
        var loadedEarlier = await archiveService.AsNoTracking().GetAsync(archive.Id);

        // ...then the conversion finished...
        await GetService<ChunkManifestService>().BuildAsync(archive.Id);

        var converted = await archiveService.AsNoTracking().GetAsync(archive.Id);

        converted.ManifestStatus.ShouldBe(ChunkManifestStatus.Ready);

        // ...and the admin renamed the version from the copy they had
        loadedEarlier.Version = "1.0.1";
        await archiveService.UpdateAsync(loadedEarlier);

        var saved = await archiveService.AsNoTracking().GetAsync(archive.Id);

        saved.Version.ShouldBe("1.0.1");
        saved.ManifestStatus.ShouldBe(ChunkManifestStatus.Ready);
        saved.ManifestHash.ShouldBe(converted.ManifestHash);
        saved.ManifestBuiltOn.ShouldBe(converted.ManifestBuiltOn);
    }
}

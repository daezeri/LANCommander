using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using LANCommander.SDK.Chunking;
using LANCommander.SDK.Enums;
using LANCommander.Server.Data.Models;
using LANCommander.Server.Endpoints;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using LANCommander.Server.Settings.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace LANCommander.Server.Tests.Endpoints;

/// <summary>
/// The chunk endpoints serve what the server offers, an archive's current manifest, and chunks by
/// hash, and refuse both while the download format is Archives only.
/// </summary>
/// <remarks>
/// The handlers are invoked directly, as in <see cref="ToolsEndpointsTests"/>, to stay off the
/// fixture's shared authentication token.
/// </remarks>
[Collection("Application")]
public class ChunkEndpointsTests(ApplicationFixture fixture) : BaseTest(fixture), IDisposable
{
    // Settings are shared by the whole run, so each test puts the format back as it found it
    private ChunkDownloadFormat? _originalFormat;

    private SettingsProvider<Settings.Settings> SettingsProvider => GetService<SettingsProvider<Settings.Settings>>();

    private void SetFormat(ChunkDownloadFormat format)
    {
        _originalFormat ??= SettingsProvider.CurrentValue.Server.Archives.DownloadFormat;

        SettingsProvider.CurrentValue.Server.Archives.DownloadFormat = format;
    }

    void IDisposable.Dispose()
    {
        if (_originalFormat.HasValue)
            SettingsProvider.CurrentValue.Server.Archives.DownloadFormat = _originalFormat.Value;

        Dispose();
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];

        new Random(seed).NextBytes(data);

        return data;
    }

    /// <summary>An archive with one file, converted to chunks.</summary>
    private async Task<(Archive Archive, byte[] Data)> AddConvertedArchiveAsync(byte[] data)
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
        using (var stream = zip.CreateEntry("data.bin").Open())
            stream.Write(data);

        await GetService<ChunkManifestService>().BuildAsync(archive.Id);

        return (await archiveService.AsNoTracking().GetAsync(archive.Id), data);
    }

    private Task<Results<FileStreamHttpResult, NotFound, BadRequest<string>>> GetChunkAsync(string hash, HttpContext context) =>
        ChunkEndpoints.GetChunkAsync(
            hash,
            context,
            new ClaimsPrincipal(),
            GetService<ChunkStore>(),
            GetService<DownloadThrottle>(),
            SettingsProvider);

    private Task<Results<FileContentHttpResult, NotFound>> GetManifestAsync(Guid archiveId, HttpContext context) =>
        ChunkEndpoints.GetManifestAsync(
            archiveId,
            context,
            GetService<ArchiveService>(),
            GetService<ChunkStore>(),
            SettingsProvider);

    [Theory]
    [InlineData(ChunkDownloadFormat.ArchivesOnly, false, false)]
    [InlineData(ChunkDownloadFormat.ChunksAvailable, true, false)]
    [InlineData(ChunkDownloadFormat.ChunksPreferred, true, true)]
    public void Capabilities_FollowTheDownloadFormat(ChunkDownloadFormat format, bool available, bool preferred)
    {
        SetFormat(format);

        var capabilities = ChunkEndpoints.GetCapabilities(SettingsProvider).Value!;

        capabilities.Available.ShouldBe(available);
        capabilities.Preferred.ShouldBe(preferred);
        capabilities.FormatVersion.ShouldBe(ChunkManifest.CurrentFormatVersion);
        capabilities.ChunkSize.ShouldBe(ChunkManifest.DefaultChunkSize);
    }

    [Fact]
    public async Task Routes_AreMapped()
    {
        // Whatever the shared client's sign-in, a mapped route doesn't answer 404 for these
        var client = ApplicationFixture.Instance.HttpClient;

        (await client.GetAsync(ChunkHttp.CapabilitiesRoute)).StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
        (await client.GetAsync(ChunkHttp.ManifestRoute(Guid.NewGuid()))).StatusCode.ShouldNotBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Chunk_IsServedWithItsCompressionAndDecompressesToItsHash()
    {
        SetFormat(ChunkDownloadFormat.ChunksAvailable);

        // Repetitive, so it's stored compressed
        var data = Enumerable.Range(0, 50_000).Select(i => (byte)(i % 5)).ToArray();
        await AddConvertedArchiveAsync(data);

        var hash = ChunkHash.Compute(data);
        var context = new DefaultHttpContext();

        var result = (await GetChunkAsync(hash, context)).Result.ShouldBeOfType<FileStreamHttpResult>();

        context.Response.Headers[ChunkHttp.CompressionHeader].ToString().ShouldBe("deflate");
        context.Response.Headers.CacheControl.ToString().ShouldContain("immutable");
        result.EntityTag!.Tag.ToString().ShouldBe($"\"{hash}\"");

        await using var decompressed = ChunkCodec.OpenDecompressed(result.FileStream, ChunkHttp.ParseCompression(context.Response.Headers[ChunkHttp.CompressionHeader]!));
        using var output = new MemoryStream();

        await decompressed.CopyToAsync(output);

        ChunkHash.Compute(output.ToArray()).ShouldBe(hash);
    }

    [Fact]
    public async Task Chunk_IsRefusedWhileTheFormatIsArchivesOnly()
    {
        SetFormat(ChunkDownloadFormat.ChunksAvailable);

        var data = Bytes(1000, 1);
        await AddConvertedArchiveAsync(data);

        SetFormat(ChunkDownloadFormat.ArchivesOnly);

        (await GetChunkAsync(ChunkHash.Compute(data), new DefaultHttpContext())).Result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task UnknownChunk_IsNotFound_AndAnythingButAHash_IsABadRequest()
    {
        SetFormat(ChunkDownloadFormat.ChunksAvailable);
        await EnsureChunkStorageLocationAsync();

        (await GetChunkAsync(ChunkHash.Compute(Bytes(10, 2)), new DefaultHttpContext())).Result.ShouldBeOfType<NotFound>();
        (await GetChunkAsync(new string('A', 64), new DefaultHttpContext())).Result.ShouldBeOfType<BadRequest<string>>();
    }

    [Fact]
    public async Task Manifest_IsServedWithItsHash()
    {
        SetFormat(ChunkDownloadFormat.ChunksPreferred);

        var (archive, data) = await AddConvertedArchiveAsync(Bytes(3000, 3));
        var context = new DefaultHttpContext();

        var result = (await GetManifestAsync(archive.Id, context)).Result.ShouldBeOfType<FileContentHttpResult>();
        var bytes = result.FileContents.ToArray();

        context.Response.Headers[ChunkHttp.ManifestHashHeader].ToString().ShouldBe(archive.ManifestHash);
        ChunkHash.Compute(bytes).ShouldBe(archive.ManifestHash);

        var manifest = ChunkManifestSerializer.Deserialize(bytes);

        manifest.Files.Single().Chunks.ShouldBe([ChunkHash.Compute(data)]);
    }

    [Fact]
    public async Task Manifest_IsAlsoServedAsAChunkByItsHash()
    {
        SetFormat(ChunkDownloadFormat.ChunksAvailable);

        var (archive, _) = await AddConvertedArchiveAsync(Bytes(200, 4));

        (await GetChunkAsync(archive.ManifestHash!, new DefaultHttpContext())).Result.ShouldBeOfType<FileStreamHttpResult>();
    }

    [Fact]
    public async Task Manifest_OfAnUnconvertedArchive_IsNotFound()
    {
        SetFormat(ChunkDownloadFormat.ChunksAvailable);

        var game = await GetService<GameService>().AddAsync(new Game { Title = $"Unconverted {Guid.NewGuid():N}" });
        var storageLocation = await GetService<StorageLocationService>().FirstOrDefaultAsync(l => l.Type == StorageLocationType.Archive)
            ?? await GetService<StorageLocationService>().AddAsync(new StorageLocation { Path = GetTemporaryDirectory(), Type = StorageLocationType.Archive });

        var archive = await GetService<ArchiveService>().AddAsync(new Archive
        {
            GameId = game.Id,
            ObjectKey = Guid.NewGuid().ToString(),
            Version = "1.0",
            StorageLocationId = storageLocation.Id,
        });

        (await GetManifestAsync(archive.Id, new DefaultHttpContext())).Result.ShouldBeOfType<NotFound>();
        (await GetManifestAsync(Guid.NewGuid(), new DefaultHttpContext())).Result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task Manifest_IsRefusedWhileTheFormatIsArchivesOnly()
    {
        SetFormat(ChunkDownloadFormat.ChunksAvailable);

        var (archive, _) = await AddConvertedArchiveAsync(Bytes(500, 5));

        SetFormat(ChunkDownloadFormat.ArchivesOnly);

        (await GetManifestAsync(archive.Id, new DefaultHttpContext())).Result.ShouldBeOfType<NotFound>();
    }
}

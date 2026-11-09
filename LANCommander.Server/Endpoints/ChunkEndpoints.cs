using System.Security.Claims;
using LANCommander.SDK.Chunking;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Services;
using LANCommander.Server.Services.Chunks;
using LANCommander.Server.Settings.Enums;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace LANCommander.Server.Endpoints;

/// <summary>
/// Chunk downloads for LAN peer-to-peer: what the server offers, an archive's current manifest, and
/// chunks by hash. Manifests are stored as chunks too, so a manifest can also be fetched by its hash.
/// While the download format is <see cref="ChunkDownloadFormat.ArchivesOnly"/>, manifests and chunks
/// answer 404, as on a server without chunks.
/// </summary>
public static class ChunkEndpoints
{
    /// <summary>
    /// Chunks never change, but they are served only to signed-in users, so only the browser or client
    /// that fetched one keeps it.
    /// </summary>
    private const string ImmutableCacheControl = "private, max-age=31536000, immutable";

    public static void MapChunkEndpoints(this IEndpointRouteBuilder routes)
    {
        var chunks = routes.MapGroup("/api/Chunks").RequireAuthorization();

        chunks.MapGet("/Capabilities", GetCapabilities);
        chunks.MapGet("/{hash:length(64)}", GetChunkAsync);

        routes.MapGroup("/api/Archives").RequireAuthorization()
            .MapGet("/{id:guid}/ChunkManifest", GetManifestAsync);
    }

    internal static Ok<ChunkCapabilities> GetCapabilities(
        [FromServices] SettingsProvider<Settings.Settings> settingsProvider)
    {
        var format = settingsProvider.CurrentValue.Server.Archives.DownloadFormat;

        return TypedResults.Ok(new ChunkCapabilities
        {
            FormatVersion = ChunkManifest.CurrentFormatVersion,
            ChunkSize = ChunkManifest.DefaultChunkSize,
            Available = format != ChunkDownloadFormat.ArchivesOnly,
            Preferred = format == ChunkDownloadFormat.ChunksPreferred,
        });
    }

    /// <summary>
    /// A chunk's stored bytes, compressed or not as <see cref="ChunkHttp.CompressionHeader"/> says. The
    /// client checks them against the hash after decompressing.
    /// </summary>
    internal static async Task<Results<FileStreamHttpResult, NotFound, BadRequest<string>>> GetChunkAsync(
        string hash,
        HttpContext httpContext,
        ClaimsPrincipal user,
        [FromServices] ChunkStore chunkStore,
        [FromServices] DownloadThrottle downloadThrottle,
        [FromServices] SettingsProvider<Settings.Settings> settingsProvider)
    {
        if (!IsOffered(settingsProvider))
            return TypedResults.NotFound();

        if (!ChunkHash.IsValid(hash))
            return TypedResults.BadRequest("A chunk hash is 64 lowercase hex characters");

        var opened = await chunkStore.OpenStoredAsync(hash);

        if (opened == null)
            return TypedResults.NotFound();

        var (stream, chunk) = opened.Value;

        httpContext.Response.Headers[ChunkHttp.CompressionHeader] = ChunkHttp.ToHeaderValue(chunk.Compression);
        httpContext.Response.Headers.CacheControl = ImmutableCacheControl;

        var throttled = await downloadThrottle.ApplyAsync(stream, user);

        return TypedResults.File(
            throttled,
            "application/octet-stream",
            entityTag: new EntityTagHeaderValue($"\"{hash}\""));
    }

    /// <summary>
    /// The archive's current manifest, uncompressed, with its hash in
    /// <see cref="ChunkHttp.ManifestHashHeader"/>. Only a Ready manifest is served: an out-of-date one
    /// no longer describes the archive.
    /// </summary>
    internal static async Task<Results<FileContentHttpResult, NotFound>> GetManifestAsync(
        Guid id,
        HttpContext httpContext,
        [FromServices] ArchiveService archiveService,
        [FromServices] ChunkStore chunkStore,
        [FromServices] SettingsProvider<Settings.Settings> settingsProvider)
    {
        if (!IsOffered(settingsProvider))
            return TypedResults.NotFound();

        var archive = await archiveService.AsNoTracking().GetAsync(id);

        if (archive is not { ManifestStatus: ChunkManifestStatus.Ready } || !ChunkHash.IsValid(archive.ManifestHash!))
            return TypedResults.NotFound();

        var bytes = await chunkStore.ReadAsync(archive.ManifestHash!);

        if (bytes == null)
            return TypedResults.NotFound();

        httpContext.Response.Headers[ChunkHttp.ManifestHashHeader] = archive.ManifestHash;

        // The archive's manifest can change when it's rebuilt, so this response isn't cached for long;
        // fetching the manifest by its hash from the chunk route can be
        httpContext.Response.Headers.CacheControl = "private, no-cache";

        return TypedResults.Bytes(
            bytes,
            "application/json",
            entityTag: new EntityTagHeaderValue($"\"{archive.ManifestHash}\""));
    }

    private static bool IsOffered(SettingsProvider<Settings.Settings> settingsProvider) =>
        settingsProvider.CurrentValue.Server.Archives.DownloadFormat != ChunkDownloadFormat.ArchivesOnly;
}

using LANCommander.SDK.Chunking;
using LANCommander.Server.Data;
using LANCommander.Server.Data.Enums;
using LANCommander.Server.Data.Models;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LANCommander.Server.Tests.Data;

/// <summary>
/// The chunk tables: one row per distinct chunk, linked to archives by (archive, hash). Deleting an
/// archive takes its links with it; a chunk can't be deleted while an archive still links to it.
/// </summary>
[Collection("Application")]
public class ChunkSchemaTests(ApplicationFixture fixture) : DalTest(fixture)
{
    private DatabaseContext CreateContext() =>
        GetService<IDbContextFactory<DatabaseContext>>().CreateDbContext();

    [Fact]
    public void ArchiveChunk_IsKeyedByArchiveAndHash()
    {
        using var context = CreateContext();

        var key = context.Model.FindEntityType(typeof(ArchiveChunk))!.FindPrimaryKey()!;

        key.Properties.Select(p => p.Name).ShouldBe([nameof(ArchiveChunk.ArchiveId), nameof(ArchiveChunk.ChunkHash)]);
    }

    [Fact]
    public void Links_GoWithTheirArchive_ButNotWithTheirChunk()
    {
        using var context = CreateContext();

        var foreignKeys = context.Model.FindEntityType(typeof(ArchiveChunk))!.GetForeignKeys().ToList();

        foreignKeys.Single(fk => fk.PrincipalEntityType.ClrType == typeof(Archive)).DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
        foreignKeys.Single(fk => fk.PrincipalEntityType.ClrType == typeof(Chunk)).DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
    }

    [Fact]
    public void ChunkHashColumns_HoldExactlyAHash()
    {
        using var context = CreateContext();

        context.Model.FindEntityType(typeof(Chunk))!.FindProperty(nameof(Chunk.Hash))!.GetMaxLength().ShouldBe(ChunkHash.Length);
        context.Model.FindEntityType(typeof(Archive))!.FindProperty(nameof(Archive.ManifestHash))!.GetMaxLength().ShouldBe(ChunkHash.Length);
    }

    [Fact]
    public async Task NewArchive_IsNotConverted()
    {
        var game = await AddGameAsync();
        var archive = await AddArchiveAsync(game.Id);

        archive.ManifestStatus.ShouldBe(ChunkManifestStatus.None);
        archive.ManifestHash.ShouldBeNull();
    }

    [Fact]
    public async Task ChunkAndLinks_RoundTrip()
    {
        var game = await AddGameAsync();
        var archive = await AddArchiveAsync(game.Id);
        var hash = ChunkHash.Compute(Guid.NewGuid().ToByteArray());

        await using (var context = CreateContext())
        {
            context.Add(new Chunk { Hash = hash, Size = 16, StoredSize = 16, Compression = ChunkCompression.None, CreatedOn = DateTime.UtcNow });
            context.Add(new ArchiveChunk { ArchiveId = archive.Id, ChunkHash = hash });

            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var loaded = await context.Set<Archive>()
                .Include(a => a.Chunks!)
                .ThenInclude(ac => ac.Chunk)
                .FirstAsync(a => a.Id == archive.Id);

            loaded.Chunks!.Single().Chunk.Size.ShouldBe(16);
        }
    }
}

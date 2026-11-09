using System.Text;
using LANCommander.SDK.Chunking;

namespace LANCommander.SDK.Tests.Chunking;

/// <summary>
/// Manifests serialize to compact camelCase JSON, read back unchanged, and are named by the hash of
/// those bytes. Formats newer than this SDK understands are refused rather than misread.
/// </summary>
public class ChunkManifestSerializerTests
{
    private static ChunkManifest Sample() => new()
    {
        ChunkSize = 4096,
        Directories = ["maps/"],
        Files =
        [
            new ChunkManifestFile
            {
                Path = "maps/de_dust.bsp",
                Size = 5000,
                Crc32 = 0xA1B2C3D4,
                Modified = new DateTimeOffset(2004, 6, 1, 12, 0, 0, TimeSpan.Zero),
                Chunks = [new string('a', 64), new string('b', 64)],
            },
        ],
    };

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var bytes = ChunkManifestSerializer.Serialize(Sample());
        var manifest = ChunkManifestSerializer.Deserialize(bytes);

        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(4096, manifest.ChunkSize);
        Assert.Equal("sha256", manifest.Hash);
        Assert.Equal(["maps/"], manifest.Directories);

        var file = Assert.Single(manifest.Files);

        Assert.Equal("maps/de_dust.bsp", file.Path);
        Assert.Equal(5000, file.Size);
        Assert.Equal(0xA1B2C3D4u, file.Crc32);
        Assert.Equal(new DateTimeOffset(2004, 6, 1, 12, 0, 0, TimeSpan.Zero), file.Modified);
        Assert.Equal(2, file.Chunks.Count);
    }

    [Fact]
    public void Json_IsCamelCaseWithoutComputedFields()
    {
        var json = Encoding.UTF8.GetString(ChunkManifestSerializer.Serialize(Sample()));

        Assert.Contains("\"formatVersion\":1", json);
        Assert.Contains("\"chunkSize\":4096", json);
        Assert.Contains("\"crc32\":", json);
        Assert.DoesNotContain("totalSize", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void Hash_IsStableForTheSameManifest()
    {
        var first = ChunkManifestSerializer.ComputeHash(ChunkManifestSerializer.Serialize(Sample()));
        var second = ChunkManifestSerializer.ComputeHash(ChunkManifestSerializer.Serialize(Sample()));

        Assert.Equal(first, second);
        Assert.True(ChunkHash.IsValid(first));
    }

    [Fact]
    public void Hash_ChangesWhenTheManifestDoes()
    {
        var changed = Sample();

        changed.Files[0].Size = 5001;

        Assert.NotEqual(
            ChunkManifestSerializer.ComputeHash(ChunkManifestSerializer.Serialize(Sample())),
            ChunkManifestSerializer.ComputeHash(ChunkManifestSerializer.Serialize(changed)));
    }

    [Fact]
    public void NewerFormat_IsRefused()
    {
        var json = "{\"formatVersion\":2,\"chunkSize\":4096,\"hash\":\"sha256\",\"files\":[]}"u8.ToArray();

        Assert.Throws<NotSupportedException>(() => ChunkManifestSerializer.Deserialize(json));
    }

    [Fact]
    public void UnknownHash_IsRefused()
    {
        var json = "{\"formatVersion\":1,\"chunkSize\":4096,\"hash\":\"md5\",\"files\":[]}"u8.ToArray();

        Assert.Throws<NotSupportedException>(() => ChunkManifestSerializer.Deserialize(json));
    }

    [Fact]
    public async Task DeserializeAsync_ReadsFromAStream()
    {
        var bytes = ChunkManifestSerializer.Serialize(Sample());

        var manifest = await ChunkManifestSerializer.DeserializeAsync(new MemoryStream(bytes));

        Assert.Equal("maps/de_dust.bsp", manifest.Files[0].Path);
    }

    [Theory]
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", true)]
    [InlineData("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", false)]
    [InlineData("2cf24dba", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData(null, false)]
    public void ChunkHash_IsValidOnlyForLowercaseSha256Hex(string? hash, bool valid)
    {
        Assert.Equal(valid, ChunkHash.IsValid(hash!));
    }

    [Fact]
    public void ChunkHash_MatchesSha256OfTheData()
    {
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            ChunkHash.Compute("hello"u8));
    }
}

using LANCommander.SDK.Chunking;

namespace LANCommander.SDK.Tests.Chunking;

public class ChunkCodecTests
{
    [Fact]
    public void Deflate_RoundTrips()
    {
        var data = Enumerable.Repeat("LANCommander "u8.ToArray(), 500).SelectMany(b => b).ToArray();

        var compressed = ChunkCodec.Compress(data);

        Assert.True(compressed.Length < data.Length);

        using var output = new MemoryStream();
        using (var stream = ChunkCodec.OpenDecompressed(new MemoryStream(compressed), ChunkCompression.Deflate))
            stream.CopyTo(output);

        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void None_ReturnsTheStoredBytes()
    {
        var data = new byte[] { 1, 2, 3 };
        var stored = new MemoryStream(data);

        Assert.Same(stored, ChunkCodec.OpenDecompressed(stored, ChunkCompression.None));
    }
}

using System;
using System.IO;
using System.IO.Compression;

namespace LANCommander.SDK.Chunking
{
    /// <summary>How a chunk's bytes are stored or sent. A chunk's hash is always of its uncompressed bytes.</summary>
    public enum ChunkCompression
    {
        /// <summary>Stored as is: data that's already compressed, such as .pak files, video and audio.</summary>
        None,

        /// <summary>Raw DEFLATE (RFC 1951), which every .NET version reads.</summary>
        Deflate,
    }

    public static class ChunkCodec
    {
        /// <summary>Fast rather than small: a whole library is converted once, and DEFLATE's ratio barely changes.</summary>
        public const CompressionLevel Level = CompressionLevel.Fastest;

        public static byte[] Compress(ReadOnlySpan<byte> data)
        {
            using (var output = new MemoryStream(data.Length / 2 + 64))
            {
                using (var deflate = new DeflateStream(output, Level, leaveOpen: true))
                    deflate.Write(data);

                return output.ToArray();
            }
        }

        /// <summary>A stream of the chunk's uncompressed bytes, read from <paramref name="stored"/>.</summary>
        public static Stream OpenDecompressed(Stream stored, ChunkCompression compression)
        {
            switch (compression)
            {
                case ChunkCompression.None:
                    return stored;

                case ChunkCompression.Deflate:
                    return new DeflateStream(stored, CompressionMode.Decompress);

                default:
                    throw new NotSupportedException($"Chunk compression {compression} isn't supported");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace LANCommander.SDK.Chunking
{
    /// <summary>
    /// Describes the extracted contents of an archive as files cut into fixed-size chunks, each named by
    /// the SHA-256 of its uncompressed bytes. Chunk <c>n</c> of a file starts at <c>n * ChunkSize</c>, so
    /// a client can write chunks in any order and fetch them from any source that has them.
    /// </summary>
    public class ChunkManifest
    {
        /// <summary>The newest manifest format this version of the SDK reads and writes.</summary>
        public const int CurrentFormatVersion = 1;

        /// <summary>4 MiB.</summary>
        public const int DefaultChunkSize = 4 * 1024 * 1024;

        public const string Sha256 = "sha256";

        public int FormatVersion { get; set; } = CurrentFormatVersion;

        /// <summary>The size of every chunk except the last of each file, in bytes.</summary>
        public int ChunkSize { get; set; } = DefaultChunkSize;

        /// <summary>The hash that names chunks. Always <see cref="Sha256"/> in format version 1.</summary>
        public string Hash { get; set; } = Sha256;

        /// <summary>Directories to create, with a trailing slash, including empty ones.</summary>
        public List<string> Directories { get; set; } = new List<string>();

        public List<ChunkManifestFile> Files { get; set; } = new List<ChunkManifestFile>();

        /// <summary>The total uncompressed size of every file.</summary>
        [JsonIgnore]
        public long TotalSize => Files.Sum(f => f.Size);

        /// <summary>Every distinct chunk the manifest refers to.</summary>
        public IEnumerable<string> GetDistinctChunks() => Files.SelectMany(f => f.Chunks).Distinct(StringComparer.Ordinal);
    }

    public class ChunkManifestFile
    {
        /// <summary>The path relative to the install directory, with forward slashes.</summary>
        public string Path { get; set; }

        public long Size { get; set; }

        /// <summary>The CRC-32 of the whole file, as the zip records it, so existing CRC checks keep working.</summary>
        public uint Crc32 { get; set; }

        /// <summary>The last write time recorded in the archive, if any.</summary>
        public DateTimeOffset? Modified { get; set; }

        /// <summary>The file's chunks in order. An empty file has none.</summary>
        public List<string> Chunks { get; set; } = new List<string>();

        /// <summary>The size of the chunk at <paramref name="index"/>; only the last one may be short.</summary>
        public int GetChunkLength(int index, int chunkSize)
        {
            if (index < 0 || index >= Chunks.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var offset = (long)index * chunkSize;

            return (int)Math.Min(chunkSize, Size - offset);
        }
    }
}

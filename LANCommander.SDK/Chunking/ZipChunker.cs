using System;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace LANCommander.SDK.Chunking
{
    /// <summary>Converts a zip archive to a <see cref="ChunkManifest"/> and its chunks.</summary>
    public static class ZipChunker
    {
        /// <summary>
        /// Reads every entry of <paramref name="archive"/> in order, handing each new chunk to
        /// <paramref name="onChunk"/>, and returns the manifest. Throws <see cref="ChunkManifestException"/>
        /// for an unsafe or duplicate path, or an entry whose contents don't match its CRC.
        /// </summary>
        public static async Task<ChunkManifest> BuildManifestAsync(
            ZipArchive archive,
            Func<ChunkData, CancellationToken, Task> onChunk,
            int chunkSize = ChunkManifest.DefaultChunkSize,
            CancellationToken cancellationToken = default)
        {
            if (archive == null)
                throw new ArgumentNullException(nameof(archive));

            var builder = new ChunkManifestBuilder(chunkSize);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Directory entries end with a slash and have no contents
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                {
                    builder.AddDirectory(entry.FullName);
                    continue;
                }

                using (var stream = entry.Open())
                {
                    await builder.AddFileAsync(
                        entry.FullName,
                        entry.LastWriteTime,
                        stream,
                        entry.Crc32,
                        onChunk,
                        cancellationToken);
                }
            }

            return builder.Build();
        }
    }
}

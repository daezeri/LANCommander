using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Force.Crc32;

namespace LANCommander.SDK.Chunking
{
    /// <summary>A chunk the builder has cut, for the caller to store.</summary>
    /// <param name="Hash">The chunk's name: the SHA-256 of <paramref name="Data"/>.</param>
    /// <param name="Data">The chunk's uncompressed bytes. Only valid until the callback returns.</param>
    public readonly record struct ChunkData(string Hash, ReadOnlyMemory<byte> Data);

    /// <summary>
    /// Builds a <see cref="ChunkManifest"/> from files read one after another: each is cut into chunks of
    /// <see cref="ChunkManifest.ChunkSize"/> bytes, hashed, and handed to a callback to store. A chunk is
    /// handed over only the first time it appears in this build; storing it is idempotent anyway.
    /// </summary>
    public sealed class ChunkManifestBuilder
    {
        private readonly ChunkManifest _manifest;
        private readonly HashSet<string> _paths = new HashSet<string>(ChunkPath.Comparer);
        private readonly HashSet<string> _directories = new HashSet<string>(ChunkPath.Comparer);
        private readonly HashSet<string> _seenChunks = new HashSet<string>(StringComparer.Ordinal);

        public ChunkManifestBuilder(int chunkSize = ChunkManifest.DefaultChunkSize)
        {
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize), "The chunk size must be positive");

            _manifest = new ChunkManifest { ChunkSize = chunkSize };
        }

        public int ChunkSize => _manifest.ChunkSize;

        /// <summary>Records a directory, so it is created even if no file is put in it.</summary>
        public void AddDirectory(string path)
        {
            var normalized = ChunkPath.Normalize(path);

            if (_paths.Contains(normalized))
                throw new ChunkManifestException($"\"{normalized}\" is both a file and a directory");

            if (_directories.Add(normalized))
                _manifest.Directories.Add(normalized + "/");
        }

        /// <summary>
        /// Reads <paramref name="content"/> to its end, cutting it into chunks and handing each new one to
        /// <paramref name="onChunk"/>, then records the file.
        /// </summary>
        /// <param name="path">The file's path in the archive.</param>
        /// <param name="modified">Its last write time, if the archive records one.</param>
        /// <param name="content">Its uncompressed bytes.</param>
        /// <param name="expectedCrc32">The CRC-32 the archive records, checked against the bytes read; null to skip the check.</param>
        /// <param name="onChunk">Stores a chunk. The data is only valid until the returned task completes.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public async Task<ChunkManifestFile> AddFileAsync(
            string path,
            DateTimeOffset? modified,
            Stream content,
            uint? expectedCrc32,
            Func<ChunkData, CancellationToken, Task> onChunk,
            CancellationToken cancellationToken = default)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            if (onChunk == null)
                throw new ArgumentNullException(nameof(onChunk));

            var normalized = ChunkPath.Normalize(path);

            if (_directories.Contains(normalized))
                throw new ChunkManifestException($"\"{normalized}\" is both a file and a directory");

            // Names differing only in case collide on Windows
            if (!_paths.Add(normalized))
                throw new ChunkManifestException($"The archive has \"{normalized}\" more than once");

            var file = new ChunkManifestFile
            {
                Path = normalized,
                Modified = modified,
            };

            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            uint crc = 0;

            try
            {
                while (true)
                {
                    var length = await FillAsync(content, buffer, ChunkSize, cancellationToken);

                    if (length == 0)
                        break;

                    var data = new ReadOnlyMemory<byte>(buffer, 0, length);
                    var hash = ChunkHash.Compute(data.Span);

                    crc = Crc32Algorithm.Append(crc, buffer, 0, length);

                    file.Size += length;
                    file.Chunks.Add(hash);

                    if (_seenChunks.Add(hash))
                        await onChunk(new ChunkData(hash, data), cancellationToken);

                    if (length < ChunkSize)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            file.Crc32 = crc;

            if (expectedCrc32.HasValue && expectedCrc32.Value != crc)
                throw new ChunkManifestException(
                    $"\"{normalized}\" is damaged: the archive records CRC {expectedCrc32.Value:X8} but its contents give {crc:X8}");

            _manifest.Files.Add(file);

            return file;
        }

        public ChunkManifest Build() => _manifest;

        /// <summary>Reads until <paramref name="count"/> bytes are in or the stream ends; streams like DeflateStream return less per read.</summary>
        private static async Task<int> FillAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var total = 0;

            while (total < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);

                if (read == 0)
                    break;

                total += read;
            }

            return total;
        }
    }
}

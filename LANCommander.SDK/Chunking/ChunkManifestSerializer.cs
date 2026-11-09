using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LANCommander.SDK.Chunking
{
    /// <summary>
    /// Reads and writes manifests as compact camelCase JSON. The bytes written for a manifest are what its
    /// hash is taken over, so a manifest is stored and served exactly as serialized here.
    /// </summary>
    public static class ChunkManifestSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        public static byte[] Serialize(ChunkManifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            return JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
        }

        public static ChunkManifest Deserialize(ReadOnlySpan<byte> json) => Validate(JsonSerializer.Deserialize<ChunkManifest>(json, Options));

        public static async Task<ChunkManifest> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default) =>
            Validate(await JsonSerializer.DeserializeAsync<ChunkManifest>(stream, Options, cancellationToken));

        /// <summary>The manifest's own name in the chunk store: the hash of its serialized bytes.</summary>
        public static string ComputeHash(byte[] serialized) => ChunkHash.Compute(serialized);

        private static ChunkManifest Validate(ChunkManifest manifest)
        {
            if (manifest == null)
                throw new ChunkManifestException("The manifest is empty");

            if (manifest.FormatVersion < 1 || manifest.FormatVersion > ChunkManifest.CurrentFormatVersion)
                throw new NotSupportedException(
                    $"Chunk manifest format {manifest.FormatVersion} isn't supported; this version reads up to {ChunkManifest.CurrentFormatVersion}");

            if (!string.Equals(manifest.Hash, ChunkManifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Chunk hash \"{manifest.Hash}\" isn't supported");

            if (manifest.ChunkSize <= 0)
                throw new ChunkManifestException("The manifest's chunk size must be positive");

            return manifest;
        }
    }
}

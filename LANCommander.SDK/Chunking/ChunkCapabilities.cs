namespace LANCommander.SDK.Chunking
{
    /// <summary>
    /// What a server offers for chunk downloads, from <c>GET /api/Chunks/Capabilities</c>. A server that
    /// predates chunks answers that route with 404, which means the same as <see cref="Available"/> false.
    /// </summary>
    public class ChunkCapabilities
    {
        /// <summary>The newest manifest format the server writes.</summary>
        public int FormatVersion { get; set; }

        /// <summary>The chunk size of the manifests the server writes.</summary>
        public int ChunkSize { get; set; }

        /// <summary>Whether converted archives may be downloaded as chunks. Zips are served either way.</summary>
        public bool Available { get; set; }

        /// <summary>Whether the server asks launchers to use chunks whenever a game has them.</summary>
        public bool Preferred { get; set; }
    }

    /// <summary>The routes and headers of the chunk endpoints, shared by the server and its clients.</summary>
    public static class ChunkHttp
    {
        public const string CapabilitiesRoute = "/api/Chunks/Capabilities";

        /// <summary>A stored chunk, or a manifest, by its hash.</summary>
        public static string ChunkRoute(string hash) => $"/api/Chunks/{hash}";

        /// <summary>The current manifest of an archive that has been converted.</summary>
        public static string ManifestRoute(System.Guid archiveId) => $"/api/Archives/{archiveId}/ChunkManifest";

        /// <summary>
        /// How a chunk response's bytes are stored: "none" or "deflate" (raw DEFLATE, RFC 1951). Sent as a
        /// header of its own rather than Content-Encoding, which HTTP clients decode on their own and
        /// which defines "deflate" as the zlib format.
        /// </summary>
        public const string CompressionHeader = "X-Chunk-Compression";

        /// <summary>The hash of a manifest response's bytes, for the client to check them against.</summary>
        public const string ManifestHashHeader = "X-Manifest-Hash";

        public static string ToHeaderValue(ChunkCompression compression) => compression.ToString().ToLowerInvariant();

        public static ChunkCompression ParseCompression(string value) =>
            string.Equals(value, "deflate", System.StringComparison.OrdinalIgnoreCase) ? ChunkCompression.Deflate
            : string.IsNullOrEmpty(value) || string.Equals(value, "none", System.StringComparison.OrdinalIgnoreCase) ? ChunkCompression.None
            : throw new System.NotSupportedException($"Chunk compression \"{value}\" isn't supported");
    }
}

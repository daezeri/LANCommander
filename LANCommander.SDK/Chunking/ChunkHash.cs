using System;
using System.Linq;
using System.Security.Cryptography;

namespace LANCommander.SDK.Chunking
{
    /// <summary>Names chunks: the lowercase hex SHA-256 of their uncompressed bytes.</summary>
    public static class ChunkHash
    {
        public const int Length = 64;

        public static string Compute(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];

            SHA256.HashData(data, hash);

            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>Whether <paramref name="hash"/> is a chunk name: 64 lowercase hex characters.</summary>
        public static bool IsValid(string hash) =>
            hash != null && hash.Length == Length && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
}

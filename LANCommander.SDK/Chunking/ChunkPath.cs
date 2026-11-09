using System;
using System.Linq;

namespace LANCommander.SDK.Chunking
{
    /// <summary>Validates and normalizes the paths a manifest records.</summary>
    public static class ChunkPath
    {
        /// <summary>Paths are compared without regard to case, since installs live on Windows.</summary>
        public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// Normalizes an archive entry path to forward slashes, without a leading or trailing slash.
        /// Throws for anything that could write outside the install directory: absolute paths, drive
        /// letters, <c>..</c> segments, and colons (which also name NTFS alternate data streams).
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ChunkManifestException("An entry has no path");

            var normalized = path.Replace('\\', '/');

            if (normalized.StartsWith("/") || normalized.Contains(':'))
                throw new ChunkManifestException($"The entry \"{path}\" has an absolute path");

            normalized = normalized.TrimEnd('/');

            var segments = normalized.Split('/');

            if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
                throw new ChunkManifestException($"The entry \"{path}\" has an empty, \".\" or \"..\" path segment");

            return normalized;
        }
    }
}

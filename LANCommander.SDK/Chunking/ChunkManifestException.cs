using System;

namespace LANCommander.SDK.Chunking
{
    /// <summary>The content can't be described by a chunk manifest: an unsafe path, a duplicate, or damaged data.</summary>
    public class ChunkManifestException : Exception
    {
        public ChunkManifestException(string message) : base(message) { }
    }
}

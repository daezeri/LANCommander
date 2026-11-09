using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using LANCommander.SDK.Chunking;

namespace LANCommander.Server.Data.Models
{
    /// <summary>
    /// A chunk in the server's chunk store, named by the SHA-256 of its uncompressed bytes. One row per
    /// distinct chunk, however many archives use it; <see cref="ArchiveChunk"/> links them.
    /// </summary>
    [Table("Chunks")]
    public class Chunk
    {
        [Key]
        [MaxLength(ChunkHash.Length)]
        public string Hash { get; set; } = string.Empty;

        /// <summary>The uncompressed size.</summary>
        public long Size { get; set; }

        /// <summary>The size on disk.</summary>
        public long StoredSize { get; set; }

        public ChunkCompression Compression { get; set; }

        public DateTime CreatedOn { get; set; }

        [JsonIgnore]
        public ICollection<ArchiveChunk>? Archives { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace LANCommander.Server.Data.Models
{
    /// <summary>
    /// Links an archive to a chunk its manifest uses. A chunk with no links is used by no archive and
    /// can be removed from the store. Links go when their archive is deleted; chunks don't.
    /// </summary>
    [Table("ArchiveChunks")]
    public class ArchiveChunk
    {
        public Guid ArchiveId { get; set; }
        [JsonIgnore]
        [ForeignKey(nameof(ArchiveId))]
        [InverseProperty(nameof(Models.Archive.Chunks))]
        public Archive Archive { get; set; } = null!;

        [MaxLength(LANCommander.SDK.Chunking.ChunkHash.Length)]
        public string ChunkHash { get; set; } = string.Empty;
        [JsonIgnore]
        [ForeignKey(nameof(ChunkHash))]
        [InverseProperty(nameof(Models.Chunk.Archives))]
        public Chunk Chunk { get; set; } = null!;
    }
}

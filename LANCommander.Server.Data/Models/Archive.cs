using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using LANCommander.Server.Data.Enums;

namespace LANCommander.Server.Data.Models
{
    public class Archive : BaseModel
    {
        public string ObjectKey { get; set; }

        [Required]
        public string Version { get; set; }

        public Guid StorageLocationId { get; set; }
        [JsonIgnore]
        [ForeignKey(nameof(StorageLocationId))]
        [InverseProperty(nameof(StorageLocation.Archives))]
        public StorageLocation StorageLocation { get; set; }

        public Guid? GameId { get; set; }
        [JsonIgnore]
        [ForeignKey(nameof(GameId))]
        [InverseProperty("Archives")]
        public Game? Game { get; set; }

        public Guid? RedistributableId { get; set; }
        [JsonIgnore]
        [ForeignKey(nameof(RedistributableId))]
        [InverseProperty("Archives")]
        public Redistributable? Redistributable { get; set; }
        
        public Guid? ToolId { get; set; }
        [JsonIgnore]
        [ForeignKey(nameof(ToolId))]
        [InverseProperty("Archives")]
        public Tool? Tool { get; set; }

        public Guid? GameVersionId { get; set; }
        [JsonIgnore]
        [ForeignKey(nameof(GameVersionId))]
        [InverseProperty(nameof(GameVersion.Archive))]
        public GameVersion? GameVersion { get; set; }

        [Display(Name = "Last Version")]
        public Archive? LastVersion { get; set; }

        [Display(Name = "Uncompressed Size")]
        public long UncompressedSize { get; set; }

        [Display(Name = "Compressed Size")]
        public long CompressedSize { get; set; }

        /// <summary>Where the archive is in its conversion to chunks for peer-to-peer downloads.</summary>
        [Display(Name = "Chunk Manifest")]
        public ChunkManifestStatus ManifestStatus { get; set; } = ChunkManifestStatus.None;

        /// <summary>The SHA-256 naming the archive's manifest in the chunk store, once converted.</summary>
        [MaxLength(LANCommander.SDK.Chunking.ChunkHash.Length)]
        public string? ManifestHash { get; set; }

        /// <summary>Why the last conversion failed.</summary>
        public string? ManifestError { get; set; }

        public DateTime? ManifestBuiltOn { get; set; }

        /// <summary>The chunks the archive's manifest uses, including the manifest itself.</summary>
        [JsonIgnore]
        public ICollection<ArchiveChunk>? Chunks { get; set; }
    }
}

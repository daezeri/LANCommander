using System.ComponentModel.DataAnnotations;

namespace LANCommander.Server.Settings.Enums;

/// <summary>Which download format the server offers launchers.</summary>
public enum ChunkDownloadFormat
{
    /// <summary>Zip archives only, as before chunks existed. The chunk endpoints refuse requests.</summary>
    [Display(Name = "Archives only")]
    ArchivesOnly,

    /// <summary>Chunks are offered for archives that have been converted; zips are still served.</summary>
    [Display(Name = "Chunks available")]
    ChunksAvailable,

    /// <summary>As ChunksAvailable, and launchers are asked to use chunks whenever a game has them.</summary>
    [Display(Name = "Chunks preferred")]
    ChunksPreferred,
}

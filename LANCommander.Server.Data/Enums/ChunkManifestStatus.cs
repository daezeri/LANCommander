namespace LANCommander.Server.Data.Enums;

/// <summary>Where an archive is in its conversion to chunks.</summary>
public enum ChunkManifestStatus
{
    /// <summary>Never converted.</summary>
    None,

    /// <summary>Queued or being converted.</summary>
    Pending,

    /// <summary>Converted: the manifest and every chunk it names are stored.</summary>
    Ready,

    /// <summary>Converted, but the archive has changed since, so the manifest must be rebuilt.</summary>
    Stale,

    /// <summary>The conversion failed; the reason is in <see cref="Models.Archive.ManifestError"/>.</summary>
    Failed,
}

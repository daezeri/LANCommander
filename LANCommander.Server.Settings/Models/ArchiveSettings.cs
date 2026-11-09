namespace LANCommander.Server.Settings.Models;

public class ArchiveSettings
{
    public bool EnablePatching { get; set; } = false;
    public bool AllowInsecureDownloads { get; set; } = false;
    public int MaxChunkSize { get; set; } = 50;

    /// <summary>
    /// Whether archives may be converted to chunk manifests, for LAN peer-to-peer downloads. The zips
    /// are kept and still served as before.
    /// </summary>
    public bool GenerateChunkManifests { get; set; } = false;
}
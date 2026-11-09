using Hangfire;
using LANCommander.Server.Services.Chunks;

namespace LANCommander.Server.Jobs.Background
{
    /// <summary>
    /// Queues archives for conversion to chunks: marks each Pending, so the archive list shows it at
    /// once, and hands it to <see cref="BuildChunkManifestBackgroundJob"/>.
    /// </summary>
    public sealed class ChunkManifestQueue(
        ChunkManifestService chunkManifestService,
        SettingsProvider<Settings.Settings> settingsProvider,
        IBackgroundJobClient backgroundJobs,
        ILogger<ChunkManifestQueue> logger)
    {
        public bool Enabled => settingsProvider.CurrentValue.Server.Archives.GenerateChunkManifests;

        /// <summary>Queues the archive whatever the setting says: an administrator asked for it.</summary>
        public async Task QueueAsync(Guid archiveId)
        {
            await chunkManifestService.MarkPendingAsync(archiveId);

            backgroundJobs.Enqueue<BuildChunkManifestBackgroundJob>(job => job.Execute(archiveId, CancellationToken.None));
        }

        /// <summary>
        /// Queues the archive if Generate Chunk Manifests is on. Never throws: the upload or repack
        /// that called it has already succeeded, and the recurring sweep picks the archive up anyway.
        /// </summary>
        public async Task<bool> QueueIfEnabledAsync(Guid archiveId)
        {
            if (!Enabled)
                return false;

            try
            {
                await QueueAsync(archiveId);

                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not queue archive {ArchiveId} for conversion to chunks", archiveId);

                return false;
            }
        }
    }
}

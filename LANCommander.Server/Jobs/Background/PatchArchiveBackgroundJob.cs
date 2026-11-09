using LANCommander.Server.Services;

namespace LANCommander.Server.Jobs.Background
{
    public class PatchArchiveBackgroundJob
    {
        private readonly ArchiveService _archiveService;
        private readonly ChunkManifestQueue _chunkManifestQueue;

        public PatchArchiveBackgroundJob(ArchiveService archiveService, ChunkManifestQueue chunkManifestQueue)
        {
            _archiveService = archiveService;
            _chunkManifestQueue = chunkManifestQueue;
        }

        public async Task Execute(Guid originalArchiveId, Guid alteredArchiveId)
        {
            var originalArchive = await _archiveService.GetAsync(originalArchiveId);
            var alteredArchive = await _archiveService.GetAsync(alteredArchiveId);

            await _archiveService.PatchArchiveAsync(originalArchive, alteredArchive);

            // Patching rewrites both zips
            await _chunkManifestQueue.QueueIfEnabledAsync(originalArchiveId);
            await _chunkManifestQueue.QueueIfEnabledAsync(alteredArchiveId);
        }
    }
}

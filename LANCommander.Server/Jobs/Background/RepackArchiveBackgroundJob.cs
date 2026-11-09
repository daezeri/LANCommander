using LANCommander.Server.Services;

namespace LANCommander.Server.Jobs.Background
{
    public class RepackArchiveBackgroundJob
    {
        private readonly ArchiveService _archiveService;
        private readonly ChunkManifestQueue _chunkManifestQueue;

        public RepackArchiveBackgroundJob(ArchiveService archiveService, ChunkManifestQueue chunkManifestQueue)
        {
            _archiveService = archiveService;
            _chunkManifestQueue = chunkManifestQueue;
        }

        public async Task Execute(Guid archiveId)
        {
            await _archiveService.RepackArchiveAsync(archiveId);

            // The repack marked a converted archive out of date; rebuild it now rather than at the next sweep
            await _chunkManifestQueue.QueueIfEnabledAsync(archiveId);
        }
    }
}

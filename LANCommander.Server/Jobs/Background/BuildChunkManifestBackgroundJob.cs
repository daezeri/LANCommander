using Hangfire;
using LANCommander.Server.Services.Chunks;

namespace LANCommander.Server.Jobs.Background
{
    /// <summary>
    /// Converts one archive to a chunk manifest. Not retried: a problem with the archive itself is
    /// recorded on it as Failed with the reason, and retrying wouldn't change the outcome.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public class BuildChunkManifestBackgroundJob
    {
        private readonly ChunkManifestService _chunkManifestService;

        public BuildChunkManifestBackgroundJob(ChunkManifestService chunkManifestService)
        {
            _chunkManifestService = chunkManifestService;
        }

        public async Task Execute(Guid archiveId, CancellationToken cancellationToken)
        {
            await _chunkManifestService.BuildAsync(archiveId, cancellationToken);
        }
    }
}

using LANCommander.Server.Services.Chunks;

namespace LANCommander.Server.Jobs.Recurring;

/// <summary>
/// Every 10 minutes, while Generate Chunk Manifests is on: converts a few archives that are waiting,
/// oldest first, one after another. This converts an existing library over time and catches any
/// archive that was added without being queued (imports, copies from local files). "Build all now"
/// runs the same conversion without the limit.
/// </summary>
public sealed class ChunkManifestSweep(
    ChunkManifestService chunkManifestService,
    SettingsProvider<Settings.Settings> settingsProvider,
    ILogger<ChunkManifestSweep> logger) : BaseRecurringJob(logger)
{
    // One sweep at a time in this process, scheduled or asked for: two would convert the same archives
    private static readonly SemaphoreSlim Running = new(1, 1);

    private static int _interruptedReset;

    public override string CronExpression => "*/10 * * * *";

    public override async Task ExecuteAsync()
    {
        var settings = settingsProvider.CurrentValue.Server.Archives;

        if (!settings.GenerateChunkManifests)
            return;

        await ConvertAsync(Math.Max(1, settings.ChunkSweepBatchSize));
    }

    /// <summary>
    /// Converts up to <paramref name="limit"/> waiting archives, one at a time. Never throws: a
    /// problem with one archive is recorded on it as Failed, and the others carry on. Returns how
    /// many it tried, or 0 if another sweep is already running.
    /// </summary>
    public async Task<int> ConvertAsync(int limit)
    {
        if (!await Running.WaitAsync(0))
        {
            logger.LogDebug("A chunk manifest sweep is already running");

            return 0;
        }

        try
        {
            // Jobs queued before a restart are gone, so what they left Pending would wait forever
            if (Interlocked.Exchange(ref _interruptedReset, 1) == 0)
            {
                var reset = await chunkManifestService.ResetInterruptedAsync();

                if (reset > 0)
                    logger.LogInformation("{Count} archive(s) left converting by a restart will be converted again", reset);
            }

            var archiveIds = await chunkManifestService.GetArchivesToConvertAsync(limit);

            foreach (var archiveId in archiveIds)
            {
                try
                {
                    await chunkManifestService.BuildAsync(archiveId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Archive {ArchiveId} could not be converted to chunks", archiveId);
                }
            }

            if (archiveIds.Count > 0)
                logger.LogInformation("Chunk manifest sweep converted {Count} archive(s)", archiveIds.Count);

            return archiveIds.Count;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The chunk manifest sweep failed");

            return 0;
        }
        finally
        {
            Running.Release();
        }
    }
}

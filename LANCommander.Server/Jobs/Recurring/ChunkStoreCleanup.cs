using LANCommander.Server.Services.Chunks;

namespace LANCommander.Server.Jobs.Recurring;

/// <summary>
/// Every day at 4:00: removes chunks no archive uses any more, and files a conversion wrote but never
/// recorded, once they are a day old. Skipped while a conversion is running; the next run catches up.
/// </summary>
public sealed class ChunkStoreCleanup(
    ChunkStoreCleaner cleaner,
    ILogger<ChunkStoreCleanup> logger) : BaseRecurringJob(logger)
{
    public override string CronExpression => "0 4 * * *";

    public override Task ExecuteAsync() => RunAsync(ChunkStoreCleaner.DefaultGrace);

    /// <summary>
    /// The same, without waiting a day, for an administrator who just deleted archives. Still safe: it
    /// only runs while no conversion is, so nothing it removes is about to be linked.
    /// </summary>
    public Task CleanUpNowAsync() => RunAsync(TimeSpan.Zero);

    private async Task RunAsync(TimeSpan grace)
    {
        // Never throws, so Hangfire doesn't retry; the next run tries again
        try
        {
            await cleaner.CleanUpAsync(grace);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The chunk store cleanup failed");
        }
    }
}

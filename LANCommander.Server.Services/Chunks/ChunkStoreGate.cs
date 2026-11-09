namespace LANCommander.Server.Services.Chunks;

/// <summary>
/// Keeps conversions and the chunk store's cleanup apart. A conversion can find a chunk already
/// stored and link it, so cleanup must never decide that chunk is unused at the same moment.
/// Conversions run side by side; cleanup runs only while none is, and conversions that start
/// meanwhile wait for it. One per server process, which is how the server runs.
/// </summary>
public sealed class ChunkStoreGate
{
    private readonly SemaphoreSlim _cleanup = new(1, 1);
    private int _conversions;

    /// <summary>Held for the whole of a conversion. Waits while cleanup is running.</summary>
    public async Task<IDisposable> EnterConversionAsync(CancellationToken cancellationToken = default)
    {
        await _cleanup.WaitAsync(cancellationToken);

        try
        {
            Interlocked.Increment(ref _conversions);
        }
        finally
        {
            _cleanup.Release();
        }

        return new Release(() => Interlocked.Decrement(ref _conversions));
    }

    /// <summary>The cleanup's turn, or null if a conversion or another cleanup is running.</summary>
    public async Task<IDisposable?> TryEnterCleanupAsync()
    {
        if (!await _cleanup.WaitAsync(0))
            return null;

        if (Volatile.Read(ref _conversions) > 0)
        {
            _cleanup.Release();

            return null;
        }

        return new Release(() => _cleanup.Release());
    }

    public bool ConversionRunning => Volatile.Read(ref _conversions) > 0;

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

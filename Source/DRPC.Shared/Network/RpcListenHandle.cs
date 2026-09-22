namespace DRPC.Shared.Network;

/// <summary>
/// Server listening lifetime handle. On <see cref="Dispose"/>, stops the listener and cleans up the
/// registered peer hubs.
/// </summary>
public sealed class RpcListenHandle : IAsyncDisposable, IDisposable
{
    readonly Action? _stop;
    readonly CancellationTokenSource? _linkedCts;
    readonly Func<int>? _activeConnectionCount;
    int _disposed;

    /// <summary>Creates a handle from an optional stop action, cancellation source, and connection-count probe.</summary>
    public RpcListenHandle(Action? stop, CancellationTokenSource? linkedCts = null,
        Func<int>? activeConnectionCount = null)
    {
        _stop = stop;
        _linkedCts = linkedCts;
        _activeConnectionCount = activeConnectionCount;
    }

    /// <summary>
    /// The number of currently accepted peer hubs (sibling proposal P4 operational signal). Increases on
    /// accept and decreases on disconnect; a rough indicator of server saturation against the connection
    /// cap (<c>MaxConnections</c>).
    /// </summary>
    public int ActiveConnectionCount => _activeConnectionCount?.Invoke() ?? 0;

    /// <summary>
    /// The Task corresponding to the listening loop. Awaitable independently of <see cref="Dispose"/>;
    /// it always completes on stop or cancellation.
    /// </summary>
    public Task? ListenTask { get; init; }

    /// <summary>Stops the listener and releases the linked cancellation source.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _linkedCts?.Cancel();
        }
        catch
        {
            // A failed cancel request must not block cleanup.
        }

        try
        {
            _stop?.Invoke();
        }
        catch
        {
            // Exceptions during cleanup must not propagate to the caller.
        }

        _linkedCts?.Dispose();
    }

    /// <summary>Async counterpart of <see cref="Dispose"/>; completes synchronously.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}

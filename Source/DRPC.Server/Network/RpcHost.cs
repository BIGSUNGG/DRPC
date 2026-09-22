using System.Collections.Concurrent;
using Communication.Network.RUDP;
using Communication.Shared.Channels;
using DRPC.Shared.Network;

namespace DRPC.Server.Network;

/// <summary>
/// Owns the RUDP listener lifetime. The generated <c>{Hub}.ListenAsync</c> calls these
/// methods. Creates one hub per peer and, on stop, disposes the listener together with all
/// peer hubs.
/// </summary>
public static class RpcHost
{
    /// <summary>
    /// Starts listening on <paramref name="port"/> and creates one hub per accepted peer.
    /// Uses the transport stack defaults (unlimited concurrent connections, default connect timeout).
    /// </summary>
    /// <param name="port">Port to bind.</param>
    /// <param name="connectionKey">Shared connection key, or null for none. Both endpoints must agree.</param>
    /// <param name="hubFactory">Creates a hub for each accepted channel.</param>
    /// <param name="onConnected">Optional callback invoked per hub after acceptance.</param>
    /// <param name="cancellationToken">Cancels starting the listener.</param>
    /// <exception cref="InvalidOperationException">Bind failed or a listener is already running.</exception>
    public static Task<RpcListenHandle> ListenAsync<THub>(
        int port,
        string? connectionKey,
        Func<IMessageChannel, THub> hubFactory,
        Func<THub, Task>? onConnected = null,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
        => ListenAsync(port, 0, connectionKey, hubFactory, onConnected, cancellationToken);

    /// <summary>
    /// When <paramref name="maxConnections"/> is positive, caps concurrently accepted
    /// connections (protection against connection-exhaustion attacks — once the cap is hit,
    /// new connection attempts are rejected immediately while accepting continues).
    /// 0 (default) means unlimited.
    /// </summary>
    /// <param name="port">Port to bind.</param>
    /// <param name="maxConnections">Maximum concurrent accepted connections; 0 for unlimited.</param>
    /// <param name="connectionKey">Shared connection key, or null for none. Both endpoints must agree.</param>
    /// <param name="hubFactory">Creates a hub for each accepted channel.</param>
    /// <param name="onConnected">Optional callback invoked per hub after acceptance.</param>
    /// <param name="cancellationToken">Cancels starting the listener.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConnections"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Bind failed or a listener is already running.</exception>
    public static Task<RpcListenHandle> ListenAsync<THub>(
        int port,
        int maxConnections,
        string? connectionKey,
        Func<IMessageChannel, THub> hubFactory,
        Func<THub, Task>? onConnected = null,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
    {
        if (maxConnections < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        }

        return ListenCoreAsync(port, HubSessionFactory.CreateTransportOptions(connectionKey, 0, maxConnections),
            hubFactory, onConnected, cancellationToken);
    }

    /// <summary>
    /// Specifies transport options (key, connection cap, CRC32c integrity, etc.) in bulk via
    /// <see cref="RpcEndpointOptions"/>. Both endpoints must use the same CRC32c setting
    /// (wire incompatibility otherwise).
    /// </summary>
    /// <param name="port">Port to bind.</param>
    /// <param name="endpointOptions">Transport options to apply.</param>
    /// <param name="hubFactory">Creates a hub for each accepted channel.</param>
    /// <param name="onConnected">Optional callback invoked per hub after acceptance.</param>
    /// <param name="cancellationToken">Cancels starting the listener.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpointOptions"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Bind failed or a listener is already running.</exception>
    public static Task<RpcListenHandle> ListenWithOptionsAsync<THub>(
        int port,
        RpcEndpointOptions endpointOptions,
        Func<IMessageChannel, THub> hubFactory,
        Func<THub, Task>? onConnected = null,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
    {
        if (endpointOptions is null)
        {
            throw new ArgumentNullException(nameof(endpointOptions));
        }

        return ListenCoreAsync(port, endpointOptions.ToTransportOptions(),
            hubFactory, onConnected, cancellationToken);
    }

    static Task<RpcListenHandle> ListenCoreAsync<THub>(
        int port,
        RudpTransportOptions transportOptions,
        Func<IMessageChannel, THub> hubFactory,
        Func<THub, Task>? onConnected,
        CancellationToken cancellationToken)
        where THub : Shared.Network.HubBase
    {
        var listener = new RudpListener(System.Net.IPAddress.Any, port);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peers = new ConcurrentDictionary<THub, byte>();

        listener.Accepted += channel =>
        {
            THub hub = hubFactory(channel);
            peers.TryAdd(hub, 0);
            hub.Disconnected += () => peers.TryRemove(hub, out _);

            // A peer that died in the accept→subscribe window (dropped mid session setup)
            // never gets a disconnect event (fires once per hub lifetime). If not evicted here
            // it lingers in peers until Stop, inflating ActiveConnectionCount.
            if (hub.IsDisconnected)
            {
                peers.TryRemove(hub, out _);
            }

            if (onConnected is null)
            {
                return;
            }

            _ = NotifyAsync(hub, onConnected);

            static async Task NotifyAsync(THub target, Func<THub, Task> callback)
            {
                try
                {
                    await callback(target).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // A connect-callback exception must not kill the receive path
                    // (no console dependency — trace only).
                    System.Diagnostics.Trace.TraceError($"onConnected callback threw: {e}");
                }
            }
        };

        void Stop()
        {
            listener.Stop();
            foreach (THub hub in peers.Keys.ToArray())
            {
                hub.Dispose();
            }

            peers.Clear();
            stopped.TrySetResult(true);
        }

        try
        {
            listener.Start(transportOptions);
        }
        catch
        {
            Stop();
            linkedCts.Dispose();
            throw;
        }

        var handle = new RpcListenHandle(Stop, linkedCts, () => peers.Count) { ListenTask = stopped.Task };

        // Stop must also be observable through cancellation (so ListenTask never stays incomplete forever).
        linkedCts.Token.Register(static state => ((Action)state!).Invoke(), new Action(Stop), useSynchronizationContext: false);

        return Task.FromResult(handle);
    }
}

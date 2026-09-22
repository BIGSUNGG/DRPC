using Communication.Network.RUDP;
using Communication.Shared.Channels;
using DRPC.Shared.Network;

namespace DRPC.Client.Network;

/// <summary>
/// Assembles hubs after an RUDP connection is established. The generated
/// <c>{Hub}.ConnectAsync</c> calls these methods.
/// </summary>
public static class RpcClient
{
    /// <summary>
    /// Connects to the server and assembles a hub of <typeparamref name="THub"/> over the
    /// resulting channel. Uses the transport stack's default connect timeout (about 5 seconds).
    /// </summary>
    /// <param name="host">Server host name or address.</param>
    /// <param name="port">Server port.</param>
    /// <param name="connectionKey">Shared connection key, or null for none. Both endpoints must agree.</param>
    /// <param name="hubFactory">Creates the hub from the connected channel.</param>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    /// <exception cref="InvalidOperationException">Connection refused, host resolution failed, or retries exhausted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<THub> ConnectAsync<THub>(
        string host,
        int port,
        string? connectionKey,
        Func<IMessageChannel, THub> hubFactory,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
        => ConnectAsync(host, port, connectionKey, 0, hubFactory, cancellationToken);

    /// <summary>
    /// When <paramref name="connectTimeoutMs"/> is set, connection failure against a silent host
    /// (packet black hole) is confirmed within that time. Zero or less keeps the transport stack
    /// default (about 5 seconds).
    /// </summary>
    /// <param name="host">Server host name or address.</param>
    /// <param name="port">Server port.</param>
    /// <param name="connectionKey">Shared connection key, or null for none. Both endpoints must agree.</param>
    /// <param name="connectTimeoutMs">Connect timeout in milliseconds; 0 or less for the transport default.</param>
    /// <param name="hubFactory">Creates the hub from the connected channel.</param>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="connectTimeoutMs"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Connection refused, host resolution failed, or retries exhausted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<THub> ConnectAsync<THub>(
        string host,
        int port,
        string? connectionKey,
        int connectTimeoutMs,
        Func<IMessageChannel, THub> hubFactory,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
    {
        if (connectTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));
        }

        return await ConnectCoreAsync<THub>(host, port,
            HubSessionFactory.CreateTransportOptions(connectionKey, connectTimeoutMs),
            hubFactory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Specifies transport options (key, connect timeout, CRC32c integrity, etc.) in bulk via
    /// <see cref="RpcEndpointOptions"/>.
    /// </summary>
    /// <param name="host">Server host name or address.</param>
    /// <param name="port">Server port.</param>
    /// <param name="endpointOptions">Transport options to apply.</param>
    /// <param name="hubFactory">Creates the hub from the connected channel.</param>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpointOptions"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Connection refused, host resolution failed, or retries exhausted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<THub> ConnectWithOptionsAsync<THub>(
        string host,
        int port,
        RpcEndpointOptions endpointOptions,
        Func<IMessageChannel, THub> hubFactory,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
    {
        if (endpointOptions is null)
        {
            throw new ArgumentNullException(nameof(endpointOptions));
        }

        return ConnectCoreAsync<THub>(host, port, endpointOptions.ToTransportOptions(),
            hubFactory, cancellationToken);
    }

    /// <summary>Single path from transport-option assembly through connect and hub assembly (deduplication — structure review finding).</summary>
    static async Task<THub> ConnectCoreAsync<THub>(
        string host,
        int port,
        RudpTransportOptions transportOptions,
        Func<IMessageChannel, THub> hubFactory,
        CancellationToken cancellationToken)
        where THub : Shared.Network.HubBase
    {
        var connector = new RudpConnector();

        if (!await connector.ConnectAsync(host, port, transportOptions, cancellationToken).ConfigureAwait(false)
            || connector.Channel is null)
        {
            throw new InvalidOperationException("Failed to connect to server.");
        }

        IMessageChannel channel = connector.Channel;
        return hubFactory(channel);
    }
}

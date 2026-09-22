using Communication.Shared.Channels;
using Communication.Shared.Connection;
using Communication.Shared.Sessions;
using DRPC.Shared.Message;
using DRPC.Shared.Network;

namespace DRPC.Shared.Tests;

/// <summary>
/// Memory session for exercising only the Hub runtime, with no transport stack. Records sent messages in order.
/// </summary>
internal sealed class FakeSession : ISession
{
    public List<object> Sent { get; } = new();
    public List<SendOptions?> SentOptions { get; } = new();
    public int DisconnectCount { get; private set; }
    public Func<object, bool>? OnSend { get; set; }

    public Task SendAsync(object message) => SendAsync(message, null);

    public Task SendAsync(object message, SendOptions? options)
    {
        Sent.Add(message);
        SentOptions.Add(options);
        OnSend?.Invoke(message);
        return Task.CompletedTask;
    }

    public Task SendAndFlushAsync(object message, SendOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync(message, options);

    public void Disconnect() => DisconnectCount++;

    public bool IsConnected() => DisconnectCount == 0;

    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    public void RaiseDisconnected(DisconnectReason reason = DisconnectReason.Remote)
        => Disconnected?.Invoke(this, new DisconnectedEventArgs(reason));

    public T Expect<T>(int index) => (T)Sent[index];

    public void Clear()
    {
        Sent.Clear();
        SentOptions.Clear();
    }

    public void Dispose()
    {
    }
}

/// <summary>Exposes HubBase's protected surface (SendRPC/RequestRPC, registration dictionaries) to tests.</summary>
internal sealed class TestHub : HubBase
{
    public TestHub(ISession session)
        : base(_ => session)
    {
    }

    public void Register(int methodId, Func<byte[], Task<byte[]>> action,
        RpcDeliveryMode mode = RpcDeliveryMode.ReliableOrdered)
    {
        MethodCallActions[methodId] = action;
        MethodDeliveryModes[methodId] = mode;
    }

    /// <summary>Authorization hook injection — when unset, the default (allow all) applies.</summary>
    public Func<int, Task<bool>>? AuthorizeHandler { get; set; }

    protected override Task<bool> AuthorizeRequestAsync(int methodId)
        => AuthorizeHandler?.Invoke(methodId) ?? Task.FromResult(true);

    public new Task SendRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode)
        => base.SendRPC(methodId, parameterData, mode);

    public new Task<byte[]> RequestRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode,
        CancellationToken cancellationToken = default)
        => base.RequestRPC(methodId, parameterData, mode, cancellationToken);

    public new Task<byte[]> RequestRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode, TimeSpan? timeout,
        CancellationToken cancellationToken = default)
        => base.RequestRPC(methodId, parameterData, mode, timeout, cancellationToken);

    /// <summary>Observes the fire-and-forget dispatch task — for verifying firewall absorption.</summary>
    public Task DispatchForTest(ProcedureCallRequestMessage message) => ProcessRequestAsync(message);
}

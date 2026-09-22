using System.Buffers;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Communication.Shared.Channels;
using Communication.Shared.Connection;
using Communication.Shared.Sessions;
using DRPC.Shared.Message;
using DRPC.Shared.Network;

namespace DRPC.Benchmarks;

/// <summary>
/// HubBase hot-path benchmarks — a loopback memory session measures the runtime only (dispatch and pending-call
/// completion, excluding serialization).
/// Baseline: Document/03-Reference/Performance.md.
/// </summary>
[MemoryDiagnoser]
public class HubBenchmarks
{
    readonly byte[] _payload = { 1, 2, 3 };
    readonly byte[] _response = { 1, 2, 3 };
    BenchSession _session = null!;
    BenchHub _hub = null!;
    Action<object> _loopback = null!;

    [GlobalSetup]
    public void Setup()
    {
        _session = new BenchSession();
        _hub = new BenchHub(_ => _session);

        // Roundtrip loopback: bounce the sent request straight back as a response (excludes transport-stack cost).
        _loopback = m =>
        {
            if (m is ProcedureCallRequestMessage { CallId: not 0 } request)
            {
                _hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(request.CallId, _response));
            }
        };
        _session.Route = _loopback;
        _hub.RegisterAction(1, _ => Task.FromResult(_response));
    }

    [Benchmark(Description = "Outgoing roundtrip (loopback session)")]
    public async Task<byte[]> OutgoingRoundtrip()
        => await _hub.Roundtrip(1, _payload);

    [Benchmark(Description = "Incoming dispatch (request→impl→response)")]
    public async Task IncomingDispatch()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _session.Route = m =>
        {
            if (m is ProcedureCallResponseMessage)
            {
                done.TrySetResult();
            }
        };

        _hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 1, _payload));
        await done.Task;

        _session.Route = _loopback; // restore
    }

    [Benchmark(Description = "One-way send (CallId 0)")]
    public Task OneWaySend()
        => _hub.OneWay(2, _payload);

    [Benchmark(Description = "Converter serialize (RPC request)")]
    public long ConverterSerialize()
    {
        var writer = new ArrayBufferWriter<byte>();
        HubSessionFactory.Converter.Serialize(new ProcedureCallRequestMessage(1u, 1, _payload), writer);
        return writer.WrittenCount;
    }
}

/// <summary>Exposes HubBase's protected surface (benchmark-only).</summary>
sealed class BenchHub : HubBase
{
    public BenchHub(Func<HubBase, ISession> sessionFactory)
        : base(sessionFactory)
    {
    }

    /// <summary>Roundtrip call entry point — the loopback session completes the response immediately.</summary>
    public Task<byte[]> Roundtrip(int methodId, byte[] payload)
        => RequestRPC(methodId, payload, RpcDeliveryMode.ReliableOrdered);

    public Task OneWay(int methodId, byte[] payload)
        => SendRPC(methodId, payload, RpcDeliveryMode.ReliableOrdered);

    public void RegisterAction(int methodId, Func<byte[], Task<byte[]>> action)
        => MethodCallActions[methodId] = action;
}

/// <summary>Memory session that forwards sends to a routing callback (zero transport-stack cost).</summary>
sealed class BenchSession : ISession
{
    public Action<object>? Route { get; set; }

    public Task SendAsync(object message) => SendAsync(message, null);

    public Task SendAsync(object message, SendOptions? options)
    {
        Route?.Invoke(message);
        return Task.CompletedTask;
    }

    public Task SendAndFlushAsync(object message, SendOptions? options = null,
        System.Threading.CancellationToken cancellationToken = default)
        => SendAsync(message, options);

    public void Disconnect() { }

    public bool IsConnected() => true;

    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    public void Dispose() { }
}

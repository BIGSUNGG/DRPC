using Communication.Shared.Sessions;
using DRPC;
using DRPC.Client.Network;
using DRPC.Server.Network;
using DRPC.Shared;
using DRPC.Shared.Interface;
using DRPC.Shared.Message;
using DRPC.Shared.Network;
using Xunit;

namespace DRPC.E2E.Tests;

/// <summary>
/// Roundtrip verification over real RUDP (127.0.0.1). Exercises delivery modes, OneWay, callbacks, and error paths on the wire.
/// Each test uses a different port to keep parallel execution safe.
/// </summary>
public class RudpLoopbackTests
{
    const string Key = "e2e-key";

    static int NextPort()
    {
        // Use an OS-assigned ephemeral port — a fixed seed (9600…) collided with Windows reserved port ranges and leftover
        // listeners from earlier runs, causing "listener bind failed" flakes. The tiny race between probe-close and rebind is accepted.
        using var probe = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Default_mode_is_reliable_ordered_and_roundtrips()
    {
        var (server, client, handle) = await PairAsync();
        await using var _ = handle;

        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
        Assert.NotNull(server);
        client.Dispose();
    }

    [Fact]
    public async Task Sequenced_override_roundtrips()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        Assert.Equal("echo:udp", await Within(client.EchoAsync("udp")));
        client.Dispose();
    }

    [Fact]
    public async Task Unreliable_override_void_call_is_answered()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        // void + non-OneWay, so it still roundtrips to an empty response. Unreliable frames are not lost on the loopback.
        await Within(client.PingAsync(11));
        client.Dispose();
    }

    [Fact]
    public async Task OneWay_call_reaches_peer_without_response()
    {
        string note = "note-" + Guid.NewGuid().ToString("N");
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        await Within(client.NoteAsync(note));
        await WaitUntilAsync(() => E2EServerHub.ReceivedNotes.Contains(note));
        client.Dispose();
    }

    [Fact]
    public async Task Message_arguments_and_results_roundtrip()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        OrderSummary summary = await Within(client.PlaceOrderAsync(new Order
        {
            Item = "cup",
            Quantity = 2,
            Tags = { 1, 2, 3 },
        }));

        Assert.Equal("cupx2", summary.Receipt);
        Assert.Equal(6m, summary.Total);
        client.Dispose();
    }

    [Fact]
    public async Task Server_can_call_back_into_client_contract()
    {
        int port = NextPort();
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await E2EServerHub.ListenAsync(port, Key, async hub =>
        {
            observed.TrySetResult(await hub.ClientValueAsync());
        });
        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);

        Assert.Equal(4242, await Within(observed.Task));
    }

    [Fact]
    public async Task Group_message_keeps_runtime_type_over_the_wire()
    {
        int port = NextPort();
        string text = "shout-" + Guid.NewGuid().ToString("N");

        await using var handle = await E2EServerHub.ListenAsync(port, Key, async hub =>
        {
            await hub.ReceiveLineAsync(new ShoutChatLine { Text = text });
        });
        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);

        await WaitUntilAsync(() => E2EClientHub.ReceivedLines.Any(line => line.EndsWith(":" + text, StringComparison.Ordinal)
            && line.StartsWith("ShoutChatLine:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Connect_timeout_bounds_silent_host_failure()
    {
        // A port nobody listens on (black hole): grab an ephemeral port, then close it.
        int silentPort;
        using (var probe = new System.Net.Sockets.UdpClient(0))
        {
            silentPort = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 300ms bound: the failure must be confirmed within it — not at the LiteNetLib default (~5s).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectAsync("127.0.0.1", silentPort, Key, 300,
                channel => new RogueClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"connect failure took {stopwatch.Elapsed} — timeout did not bound the silent host.");
    }

    [Fact]
    public async Task Cancellation_ends_wait_quickly_and_session_stays_usable()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // The server implementation delays 2s (Slow) — the token budget (300ms) must cut the wait first (cancellation verified on the wire).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Within(client.SlowAsync(2000, cts.Token), 5000));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"cancellation took {stopwatch.Elapsed} — token budget did not bound the wait");

        // Cancellation must not poison the session — a follow-up call over the same connection roundtrips normally.
        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
    }

    [Fact]
    public async Task Per_call_timeout_ends_wait_and_session_stays_usable()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);

        // The server implementation delays 3s — the per-call budget of 400ms (including the 1s timeout scan tick, ≲2s) cuts the wait instead of the hub default (30s).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => Within(client.SlowWithPerCallTimeoutAsync(3000), 5000));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"per-call timeout took {stopwatch.Elapsed} — budget did not bound the wait");

        // Expiry must not poison the session — a follow-up call over the same connection roundtrips normally (the late response is discarded).
        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
    }

    [Fact]
    public async Task Listener_max_connections_rejects_excess_peer()
    {
        int port = NextPort();

        // Limit of 1: defends against connection-exhaustion attacks — excess peers are rejected immediately while acceptance continues.
        await using var handle = await RpcHost.ListenAsync(port, 1, Key,
            channel => new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)),
            _ => Task.CompletedTask);

        using var first = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        Assert.Equal(5, await Within(first.AddAsync(2, 3))); // a client within the limit works normally

        // Over the limit — ConnectAsync fails via an immediate rejection notice, not by exhausting retries.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectAsync("127.0.0.1", port, Key,
                channel => new RogueClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));
    }

    [Fact]
    public async Task Listener_negative_max_connections_is_rejected()
    {
        int port = NextPort();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RpcHost.ListenAsync(port, -1, Key,
            channel => new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));
    }

    [Fact]
    public async Task Crc32c_both_endpoints_roundtrip_and_mismatch_cannot_connect()
    {
        int port = NextPort();

        // CRC32c enabled on both server and client — normal roundtrips over the integrity layer.
        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, EnableCrc32c = true };
        await using var handle = await RpcHost.ListenWithOptionsAsync(port, serverOptions,
            channel => new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)),
            _ => Task.CompletedTask);

        var clientOptions = new RpcEndpointOptions { ConnectionKey = Key, EnableCrc32c = true, ConnectTimeoutMs = 3000 };
        using var client = await RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
        client.Dispose();

        // Wire-incompatibility check — a client with CRC off is treated as a checksum-violating peer and cannot connect (fails fast with the 300ms bound).
        var plainOptions = new RpcEndpointOptions { ConnectionKey = Key, ConnectTimeoutMs = 300 };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, plainOptions,
                channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));
    }

    [Fact]
    public async Task ListenHandle_exposes_active_connection_count()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        Assert.Equal(0, handle.ActiveConnectionCount); // before acceptance

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        await Within(client.AddAsync(2, 3));
        await WaitUntilAsync(() => handle.ActiveConnectionCount == 1);

        client.Dispose();
        await WaitUntilAsync(() => handle.ActiveConnectionCount == 0); // disconnect reclaim (sibling proposal P4 operational signal)
    }

    [Fact]
    public async Task Peer_dead_before_subscription_is_swept_from_active_count()
    {
        int port = NextPort();

        // A hub already dead in the accept→subscribe window — its disconnect event (once per lifetime) fired without any subscriber.
        // Without the sweep (pre-fix), this peer would keep ActiveConnectionCount at 1 until Stop.
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handle = await RpcHost.ListenAsync(port, Key, channel =>
        {
            var hub = new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub));
            hub.NotifyDisconnected(new InvalidOperationException("dead on arrival"));
            return hub;
        }, _ => { accepted.TrySetResult(); return Task.CompletedTask; });

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);

        await accepted.Task; // observes acceptance (→ sweep) completion — the counter must be 0 at this point
        await WaitUntilAsync(() => handle.ActiveConnectionCount == 0); // reclaimed immediately, without waiting
    }

    [Fact]
    public async Task Queue_options_flow_through_session_factory()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        // Queue options flow through the session factory — narrow the send frame limit to 64 bytes (sibling proposal P3: unified FrameTimeout·MaxFrameLength management).
        var queueOptions = new Communication.Shared.Messages.MessageQueueOptions { MaxFrameLength = 64 };
        using var client = await RpcClient.ConnectAsync("127.0.0.1", port, Key,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub, queueOptions)));
        client.RpcTimeout = TimeSpan.FromSeconds(2);

        // Frames within the limit roundtrip normally.
        Assert.Equal(5, await Within(client.AddAsync(2, 3)));

        // Over-limit payloads are quarantined on send with no response — fails within 2s via timeout (or the quarantine exception).
        string big = new string('x', 200);
        await Assert.ThrowsAnyAsync<Exception>(() => Within(client.EchoAsync(big)));
    }

    [Fact]
    public async Task Unknown_method_id_yields_unknown_method_fault()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        // Plants a MethodId the server does not know (a raw request bypassing the generated stubs).
        using var rogue = await RpcClient.ConnectAsync("127.0.0.1", port, Key,
            channel => new RogueClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => Within(rogue.ProbeAsync(4242)));
        Assert.Equal(RpcErrorCode.UnknownMethod, fault.ErrorCode);
    }

    [Fact]
    public async Task Implementation_exception_surfaces_as_unhandled_fault()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => Within(client.AlwaysFailsAsync()));
        Assert.Equal(RpcErrorCode.Unhandled, fault.ErrorCode);
        Assert.Contains("intentional failure", fault.Message);
        client.Dispose();
    }

    [Fact]
    public async Task Slow_response_triggers_client_timeout()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;
        client.RpcTimeout = TimeSpan.FromMilliseconds(200);

        await Assert.ThrowsAsync<TimeoutException>(() => Within(client.SlowAsync(3000), 8000));
        client.Dispose();
    }

    [Fact]
    public async Task ListenTask_completes_and_peer_notices_when_handle_is_disposed()
    {
        int port = NextPort();
        RpcListenHandle handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);
        var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => dropped.TrySetResult();

        handle.Dispose();

        await Within(handle.ListenTask!, 5000);
        await Within(dropped.Task);
        client.Dispose();
    }

    [Fact]
    public async Task Concurrent_calls_keep_their_own_results()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        Task<int>[] calls = Enumerable.Range(1, 20).Select(i => client.AddAsync(i, i)).ToArray();
        int[] results = await Within(Task.WhenAll(calls));

        Assert.Equal(Enumerable.Range(1, 20).Select(i => i * 2), results);
        client.Dispose();
    }

    [Fact]
    public async Task Return_only_generic_roundtrips_declared_types()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        Assert.Equal(0, await Within(client.GetDefaultAsync<int>()));
        Assert.Null(await Within(client.GetDefaultAsync<string>()));
        client.Dispose();
    }

    [Fact]
    public async Task Parameter_generic_infers_type_from_argument()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        // Called like a normal method, without type arguments — T is inferred from the arguments.
        Assert.Equal("System.Int32:42", await Within(client.DescribeAsync(42)));
        Assert.Equal("System.String:hi", await Within(client.DescribeAsync("hi")));
        client.Dispose();
    }

    [Fact]
    public async Task Complex_multi_slot_generic_roundtrips()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        Assert.Equal(0, await Within(client.BlendAsync<int, float, Order>(1.5f, new Order { Item = "x" })));
        Assert.Null(await Within(client.BlendAsync<string, double, ChatLine>(2.5, new ShoutChatLine { Text = "y" })));

        await WaitUntilAsync(() => E2EServerHub.ReceivedGeneric.Contains("Blend:Single:Single:Order:Order"));
        // The derived type sent as T3=ChatLine arrives intact (group polymorphism preserved).
        await WaitUntilAsync(() => E2EServerHub.ReceivedGeneric.Contains("Blend:Double:Double:ChatLine:ShoutChatLine"));
        client.Dispose();
    }

    [Fact]
    public async Task GenericMessage_parameter_roundtrips()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        await Within(client.DeliverAsync(new Package<ChatLine> { Value = new ShoutChatLine { Text = "shout" } }));
        await Within(client.DeliverAsync(new Package<Receipt> { Value = new Receipt { Tag = "cup" } }));

        await WaitUntilAsync(() => E2EServerHub.ReceivedGeneric.Contains("Deliver:ChatLine:shout"));
        await WaitUntilAsync(() => E2EServerHub.ReceivedGeneric.Contains("Deliver:Receipt:cup"));
        client.Dispose();
    }

    [Fact]
    public async Task Unknown_construction_index_yields_unhandled_fault()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        // Bypasses the generated stubs to send a construction index outside the declarations (runtime backstop check).
        using var rogue = await RpcClient.ConnectAsync("127.0.0.1", port, Key,
            channel => new RogueClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        // The payload of GetDefault (methodId 7) = a single construction-index int32.
        byte[] payload = BitConverter.GetBytes(int.MaxValue);
        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => Within(rogue.ProbeAsync(7, payload)));
        Assert.Equal(RpcErrorCode.Unhandled, fault.ErrorCode);
        Assert.Contains("unknown generic construction index", fault.Message);
    }

    [Fact]
    public async Task Validation_pass_runs_implementation()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        Assert.Equal(30, await Within(client.GuardedAddAsync(10, 20)));
        client.Dispose();
    }

    [Fact]
    public async Task Validation_failure_returns_error_code_7_without_calling_implementation()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        // GuardedReject_Implementation throws if ever invoked, so receiving code 7 is itself proof of non-invocation.
        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => Within(client.GuardedRejectAsync(1)));
        Assert.Equal(RpcErrorCode.ValidationFailed, fault.ErrorCode);
        client.Dispose();
    }

    [Fact]
    public async Task Generic_validation_gates_implementation()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        // T=int passes (default 0 roundtrips); T=string hits _Validate false → ValidationFailed(7).
        Assert.Equal(0, await Within(client.GuardedDefaultAsync<int>()));
        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => Within(client.GuardedDefaultAsync<string>()));
        Assert.Equal(RpcErrorCode.ValidationFailed, fault.ErrorCode);
        client.Dispose();
    }

    static async Task<(E2EServerHub Server, E2EClientHub Client, RpcListenHandle Handle)> PairAsync()
    {
        int port = NextPort();
        var accepted = new TaskCompletionSource<E2EServerHub>(TaskCreationOptions.RunContinuationsAsynchronously);

        RpcListenHandle handle = await E2EServerHub.ListenAsync(port, Key, hub =>
        {
            accepted.TrySetResult(hub);
            return Task.CompletedTask;
        });

        E2EClientHub client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        E2EServerHub server = await accepted.Task;
        return (server, client, handle);
    }

    static async Task<T> Within<T>(Task<T> task, int timeoutMs = 8000)
    {
        if (await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException($"The task did not complete within {timeoutMs}ms.");
        }

        return await task.ConfigureAwait(false);
    }

    static async Task Within(Task task, int timeoutMs = 8000)
    {
        if (await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException($"The task did not complete within {timeoutMs}ms.");
        }

        await task.ConfigureAwait(false);
    }

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("The condition was not met within the allotted time.");
    }
}

/// <summary>
/// A client that sends raw requests without generated stubs (used to exercise unregistered MethodId paths).
/// The contracts are left empty to suppress stub generation, and the hub itself must be partial to avoid DRPCGEN001.
/// </summary>
public interface IRogueServerProcedures : IServerProcedureDeclarations
{
}

public interface IRogueClientProcedures : IClientProcedureDeclarations
{
}

public partial class RogueClientHub : ClientHub<IRogueServerProcedures, IRogueClientProcedures>
{
    public RogueClientHub(Func<HubBase, ISession> sessionFactory)
        : base(sessionFactory)
    {
    }

    public Task<byte[]> ProbeAsync(int methodId)
        => RequestRPC(methodId, Array.Empty<byte>(), RpcDeliveryMode.ReliableOrdered);

    public Task<byte[]> ProbeAsync(int methodId, byte[] parameterData)
        => RequestRPC(methodId, parameterData, RpcDeliveryMode.ReliableOrdered);
}

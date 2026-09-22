using Communication.Network.RUDP;
using DRPC.Shared;
using DRPC.Shared.Message;
using DRPC.Shared.Network;
using Xunit;

namespace DRPC.Shared.Tests;

/// <summary>
/// HubBase unit tests: CallId, timeouts, concurrency, error paths, and delivery-mode wiring. Observed via FakeSession with no network.
/// </summary>
public class HubBaseTests
{
    static readonly byte[] Payload = { 1, 2, 3 };

    [Fact]
    public async Task SendRPC_OneWay_UsesCallIdZero()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        await hub.SendRPC(7, Payload, RpcDeliveryMode.ReliableOrdered);

        var request = First<ProcedureCallRequestMessage>(session);
        Assert.Equal(0u, request.CallId);
        Assert.Equal(7, request.MethodId);
        Assert.Equal(Payload, request.ParameterData);
    }

    [Fact]
    public async Task SendRPC_AppliesRequestedDeliveryMode()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        await hub.SendRPC(1, Payload, RpcDeliveryMode.Unreliable);
        await hub.SendRPC(2, Payload, RpcDeliveryMode.ReliableSequenced);

        Assert.Equal(RudpDeliveryMethod.Unreliable, Assert.IsType<RudpSendOptions>(session.SentOptions[0]).DeliveryMethod);
        Assert.Equal(RudpDeliveryMethod.ReliableSequenced, Assert.IsType<RudpSendOptions>(session.SentOptions[1]).DeliveryMethod);
    }

    [Fact]
    public async Task RequestRPC_AllocatesNonZeroMonotonicCallIds()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        Task<byte[]> first = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        Task<byte[]> second = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);

        var sent = session.Sent.OfType<ProcedureCallRequestMessage>().ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(1u, sent[0].CallId);
        Assert.Equal(2u, sent[1].CallId);

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(2u, Payload));
        Assert.Equal(Payload, await first);
        Assert.Equal(Payload, await second);
    }

    [Fact]
    public async Task RequestRPC_PerCallTimeout_MessageReportsPerCallBudget()
    {
        // Diagnostic accuracy — if the call expired on its per-call budget (50ms) but the message reported the hub default (30s), operators would suspect the wrong knob.
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromSeconds(30) };

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, TimeSpan.FromMilliseconds(50)));

        Assert.Contains("00:00:00.05", ex.Message);
        Assert.DoesNotContain("00:00:30", ex.Message);
    }

    [Fact]
    public async Task TimeoutTimer_ParksWhenPendingTableIsEmpty()
    {
        // Idle parking — when the pending table empties, the 1Hz timer is returned and re-created on the next timed call
        // (avoids idle-server cost that grows with the number of peers).
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromMilliseconds(50) };

        var timerField = typeof(HubBase).GetField("_timeoutTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // The timer is protecting the call from the moment it starts (EnsureTimeoutTimer runs synchronously before the send await).
        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        Assert.NotNull(timerField.GetValue(hub));
        await Assert.ThrowsAsync<TimeoutException>(() => pending);

        // The tick that handled the expiry parks the timer after finishing its scan — we wait by polling instead of sleeping (faster, narrower flake window).
        await WaitUntilAsync(() => timerField.GetValue(hub) is null);

        // Re-creation — the next timed call is protected by a fresh timer immediately (the expiry tick parks again on the same tick, so only the start is observable).
        Task<byte[]> second = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        Assert.NotNull(timerField.GetValue(hub));
        await Assert.ThrowsAsync<TimeoutException>(() => second);
    }

    [Fact]
    public async Task TimeoutTimer_ParksWhenOnlyInfiniteBudgetCallsRemain()
    {
        // Peers with only unlimited-budget (long-poll) calls left also park — deadline 0 is not scanned, so the tick would do nothing.
        // Before the fix (park only when the table was empty), a single unlimited call kept the 1Hz tick alive for the hub's whole lifetime.
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromMilliseconds(50) };

        var timerField = typeof(HubBase).GetField("_timeoutTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Task<byte[]> timed = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered); // timer starts protecting
        Task<byte[]> infinite = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, Timeout.InfiniteTimeSpan);
        await Assert.ThrowsAsync<TimeoutException>(() => timed); // the timed wait dies — only the unlimited call remains

        await WaitUntilAsync(() => timerField.GetValue(hub) is null); // parks — before the fix the table never emptied, keeping the tick forever

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(2u, Payload)); // the unlimited call accepts its response
        Assert.Equal(Payload, await infinite);
    }

    [Fact]
    public void IsDisconnected_LatchesAfterDisconnectNotification()
    {
        // A hub disconnected before subscription can never raise the Disconnected event — an observation signal is needed for listener reclaim (RpcHost immediate reclaim).
        var session = new FakeSession();
        using var hub = new TestHub(session);

        Assert.False(hub.IsDisconnected);
        hub.NotifyDisconnected(new InvalidOperationException("test disconnect"));
        Assert.True(hub.IsDisconnected);
    }

    [Fact]
    public async Task Dispose_WhileRequestInFlight_DoesNotLeakUnobservedException()
    {
        // Normal server-stop path — even with a request in flight, Dispose (semaphore release) must not leave an unobserved exception.
        var session = new FakeSession();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hub = new TestHub(session) { MaxConcurrentIncoming = 1 };
        hub.Register(1, async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return Payload;
        });

        // The test double (TestHub) observes the dispatch task — without the firewall (pre-fix), the ObjectDisposedException from the release race would surface here.
        Task processing = hub.DispatchForTest(new ProcedureCallRequestMessage(1u, 1, Payload));
        await started.Task;
        hub.Dispose(); // releasing the gate while a slot is held — the exact moment finally's Release can throw
        release.TrySetResult();

        await processing; // the firewall absorbs the normal-shutdown race — the dispatch task must complete intact
    }

    [Fact]
    public async Task RequestRPC_Timeout_ThrowsTimeoutException()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<TimeoutException>(() => hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered));
    }

    [Fact]
    public async Task RequestRPC_PerCallTimeout_OverridesHubDefault()
    {
        // Even with the hub default at 30s, a 50ms per-call budget throws TimeoutException — cuts this call short without sacrificing the global cap.
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromSeconds(30) };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task RequestRPC_PerCallInfinite_OverridesFiniteHubDefault()
    {
        // The other direction: even with the hub default at 50ms (≈1s scan tick), choosing unlimited for this call keeps the wait alive — a late response is accepted.
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromMilliseconds(50) };

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, Timeout.InfiniteTimeSpan);
        await Task.Delay(1500);
        Assert.False(pending.IsCompleted);

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));
        Assert.Equal(Payload, await pending);
    }

    [Fact]
    public async Task RequestRPC_ZeroTimeout_WaitsIndefinitely()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.Zero };

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));
        Assert.Equal(Payload, await pending);
    }

    [Fact]
    public async Task RequestRPC_ErrorResponse_ThrowsRpcFaultException()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        Task<byte[]> pending = hub.RequestRPC(4, Payload, RpcDeliveryMode.ReliableOrdered);
        uint callId = First<ProcedureCallRequestMessage>(session).CallId;

        hub.OnReceiveRPCErrorMessage(new ProcedureCallErrorMessage(callId, RpcErrorCode.Overloaded, "busy"));

        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => pending);
        Assert.Equal(RpcErrorCode.Overloaded, fault.ErrorCode);
        Assert.Equal(callId, fault.CallId);
        Assert.Equal("busy", fault.Message);
    }

    [Fact]
    public async Task UnexpectedResponseOrError_IsIgnoredWithoutFaultingOthers()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);

        // Late or duplicate responses for a CallId with no pending entry must not complete other calls with wrong values.
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(999u, new byte[] { 7 }));
        hub.OnReceiveRPCErrorMessage(new ProcedureCallErrorMessage(999u, RpcErrorCode.Unhandled, "late"));
        Assert.False(pending.IsCompleted);

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));
        Assert.Equal(Payload, await pending);
    }

    [Fact]
    public async Task CancelPendingCalls_FailsWaitingRequest()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        hub.CancelPendingCalls(new InvalidOperationException("gone"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    [Fact]
    public async Task Incoming_UnknownMethod_SendsUnknownMethodError()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(5u, 4242, Payload));

        await WaitUntilAsync(() => session.Sent.Count > 0);
        var error = First<ProcedureCallErrorMessage>(session);
        Assert.Equal(5u, error.CallId);
        Assert.Equal(RpcErrorCode.UnknownMethod, error.ErrorCode);
    }

    [Fact]
    public async Task Incoming_Success_SendsResponse_WithRequestDeliveryMode()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        hub.Register(3, data => Task.FromResult(new byte[] { 9 }), RpcDeliveryMode.ReliableUnordered);

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 3, Payload));

        await WaitUntilAsync(() => session.Sent.Count > 0);
        Assert.Equal(new byte[] { 9 }, First<ProcedureCallResponseMessage>(session).ReturnData);
        Assert.Equal(RudpDeliveryMethod.ReliableUnordered,
            Assert.IsType<RudpSendOptions>(session.SentOptions[0]).DeliveryMethod);
    }

    [Fact]
    public async Task Incoming_ImplementationThrows_SendsUnhandledError()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        hub.Register(3, _ => Task.FromException<byte[]>(new InvalidOperationException("boom")));

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 3, Payload));

        await WaitUntilAsync(() => session.Sent.Count > 0);
        var error = First<ProcedureCallErrorMessage>(session);
        Assert.Equal(RpcErrorCode.Unhandled, error.ErrorCode);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public async Task Incoming_OneWay_IsNotAnswered()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Register(3, _ =>
        {
            called.TrySetResult();
            return Task.FromResult(Payload);
        });

        // One-way signals use CallId 0 (not in the receiver's pending table)
        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(0u, 3, Payload));

        await called.Task;
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task Incoming_UnknownMethod_OneWay_IsNotAnswered()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(0u, 4242, Payload));
        await Task.Delay(100);

        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task MaxConcurrentIncoming_RejectsWhenFull()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { MaxConcurrentIncoming = 1 };

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Register(1, _ =>
        {
            started.TrySetResult();
            return gate.Task;
        });

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 1, Payload));
        await started.Task;

        // An over-limit request receives Overloaded without waiting for processing.
        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(2u, 1, Payload));
        await WaitUntilAsync(() => session.Sent.OfType<ProcedureCallErrorMessage>().Any());

        ProcedureCallErrorMessage error = Assert.Single(session.Sent.OfType<ProcedureCallErrorMessage>());
        Assert.Equal(2u, error.CallId);
        Assert.Equal(RpcErrorCode.Overloaded, error.ErrorCode);

        gate.SetResult(Payload);
    }

    [Fact]
    public void MaxConcurrentIncoming_RejectsNegative()
    {
        using var hub = new TestHub(new FakeSession());
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.MaxConcurrentIncoming = -1);
    }

    [Fact]
    public async Task MaxPendingCalls_FailsFastWhenWaitTableIsFull()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { MaxPendingCalls = 2 };

        Task<byte[]> first = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        Task<byte[]> second = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);

        // Once the limit is reached, a new call fails immediately instead of queueing (fail-fast).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered));

        // Once a slot frees up (a response arrives), a retry is accepted.
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));
        Assert.Equal(Payload, await first);

        Task<byte[]> third = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(3u, Payload));
        Assert.Equal(Payload, await third);

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(2u, Payload));
        Assert.Equal(Payload, await second);
    }

    [Fact]
    public async Task MaxPendingCalls_DefaultIsUnlimited()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);

        // The default (0 = unlimited) accepts calls regardless of the wait-table size.
        Task<byte[]>[] calls = Enumerable.Range(0, 8)
            .Select(_ => hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered))
            .ToArray();

        for (uint callId = 1; callId <= 8; callId++)
        {
            hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(callId, Payload));
        }

        foreach (Task<byte[]> call in calls)
        {
            Assert.Equal(Payload, await call);
        }
    }

    [Fact]
    public void Disconnect_reason_is_observable_and_defaults_null()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        Communication.Shared.Connection.DisconnectReason? observedInHandler = null;
        hub.Disconnected += () => observedInHandler = hub.LastDisconnectReason;

        Assert.Null(hub.LastDisconnectReason); // null before disconnection

        // The receive path (DRPCMessageHandler) invokes this from the session event — it records the reason and then raises the event.
        hub.NotifyDisconnected(new InvalidOperationException("flow"),
            Communication.Shared.Connection.DisconnectReason.FlowControl);

        // The disconnect reason is readable inside the Disconnected handler (sibling proposal P4 — identifies FlowControl backpressure).
        Assert.Equal(Communication.Shared.Connection.DisconnectReason.FlowControl, observedInHandler);
        Assert.Equal(Communication.Shared.Connection.DisconnectReason.FlowControl, hub.LastDisconnectReason);
    }

    [Fact]
    public async Task Unhandled_error_sends_exception_detail_by_default()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        hub.Register(5, _ => throw new InvalidOperationException("boom-42"));

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 5, Payload));
        await WaitUntilAsync(() => session.Sent.OfType<ProcedureCallErrorMessage>().Any());

        ProcedureCallErrorMessage error = Assert.Single(session.Sent.OfType<ProcedureCallErrorMessage>());
        Assert.Equal(RpcErrorCode.Unhandled, error.ErrorCode);
        Assert.Contains("boom-42", error.Message); // default behavior — details are sent (developer convenience)
    }

    [Fact]
    public async Task SendErrorDetails_false_suppresses_remote_detail()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { SendErrorDetails = false };
        hub.Register(5, _ => throw new InvalidOperationException("boom-42"));

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 5, Payload));
        await WaitUntilAsync(() => session.Sent.OfType<ProcedureCallErrorMessage>().Any());

        ProcedureCallErrorMessage error = Assert.Single(session.Sent.OfType<ProcedureCallErrorMessage>());
        Assert.Equal(RpcErrorCode.Unhandled, error.ErrorCode);
        Assert.DoesNotContain("boom-42", error.Message); // internal details must not leak to the remote side
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void MaxPendingCalls_RejectsNegative()
    {
        using var hub = new TestHub(new FakeSession());
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.MaxPendingCalls = -1);
    }

    [Fact]
    public async Task Authorization_default_allows_requests()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        hub.Register(7, _ => Task.FromResult(Payload));

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 7, Payload));
        await WaitUntilAsync(() => session.Sent.OfType<ProcedureCallResponseMessage>().Any());

        ProcedureCallResponseMessage response = Assert.Single(session.Sent.OfType<ProcedureCallResponseMessage>());
        Assert.Equal(1u, response.CallId);
    }

    [Fact]
    public async Task Authorization_denial_returns_permission_denied_and_skips_invocation()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { AuthorizeHandler = _ => Task.FromResult(false) };
        bool invoked = false;
        hub.Register(7, _ => { invoked = true; return Task.FromResult(Payload); });

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 7, Payload));
        await WaitUntilAsync(() => session.Sent.OfType<ProcedureCallErrorMessage>().Any());

        ProcedureCallErrorMessage error = Assert.Single(session.Sent.OfType<ProcedureCallErrorMessage>());
        Assert.Equal(1u, error.CallId);
        Assert.Equal(RpcErrorCode.PermissionDenied, error.ErrorCode);
        Assert.False(invoked); // a denied call never executes the implementation
    }

    [Fact]
    public async Task Authorization_denial_drops_one_way_silently()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { AuthorizeHandler = _ => Task.FromResult(false) };
        bool invoked = false;
        hub.Register(7, _ => { invoked = true; return Task.FromResult(Payload); });

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(0u, 7, Payload)); // one-way(CallId 0)
        await Task.Delay(100);

        Assert.Empty(session.Sent);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Authorization_hook_receives_requested_method_id()
    {
        int? observed = null;
        var session = new FakeSession();
        using var hub = new TestHub(session)
        {
            AuthorizeHandler = id => { observed = id; return Task.FromResult(true); },
        };
        hub.Register(7, _ => Task.FromResult(Payload));

        hub.OnReceiveRPCRequestMessage(new ProcedureCallRequestMessage(1u, 7, Payload));
        await WaitUntilAsync(() => session.Sent.OfType<ProcedureCallResponseMessage>().Any());

        Assert.Equal(7, observed);
    }

    [Fact]
    public async Task RequestRPC_precanceled_token_never_sends()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, cts.Token));

        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task RequestRPC_cancellation_frees_slot_and_ignores_late_response()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session) { MaxPendingCalls = 1 };
        using var cts = new CancellationTokenSource();

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, cts.Token);
        Assert.False(pending.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // A late response for a canceled CallId must not complete anything.
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));

        // Slot release check — a new call is accepted even with the limit of 1.
        Task<byte[]> next = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(2u, Payload));
        Assert.Equal(Payload, await next);
    }

    [Fact]
    public async Task Disconnect_CancelsPending_RaisesOnce_AndDisconnectsSession()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        int raised = 0;
        hub.Disconnected += () => raised++;

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        hub.Disconnect();
        hub.Disconnect();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal(1, raised);
        Assert.Equal(2, session.DisconnectCount);
    }

    [Fact]
    public async Task SessionDisconnectedEvent_CancelsPendingThroughHandler()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        var handler = new DRPCMessageHandler(session, hub);
        int raised = 0;
        hub.Disconnected += () => raised++;

        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        session.RaiseDisconnected();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal(1, raised);
        GC.KeepAlive(handler);
    }

    [Fact]
    public async Task Handler_RoutesEachMessageTypeToHub()
    {
        var session = new FakeSession();
        using var hub = new TestHub(session);
        var handler = new DRPCMessageHandler(session, hub);
        hub.Register(3, data => Task.FromResult(data), RpcDeliveryMode.ReliableOrdered);

        handler.HandleMessage(new ProcedureCallRequestMessage(1u, 3, Payload));
        await WaitUntilAsync(() => session.Sent.Count > 0);
        Assert.IsType<ProcedureCallResponseMessage>(session.Sent[0]);

        // Unregistered types are only ignored (they must not kill the receive path).
        handler.HandleMessage("not an rpc message");
        Assert.Single(session.Sent);
        GC.KeepAlive(handler);
    }

    [Fact]
    public void RpcDeliveryMap_CoversEveryMode()
    {
        // This test catches any drift between the DRPC contract enum and the RUDP enum.
        Assert.Equal(RudpDeliveryMethod.ReliableOrdered, RpcDeliveryMode.ReliableOrdered.ToSendOptions().DeliveryMethod);
        Assert.Equal(RudpDeliveryMethod.ReliableUnordered, RpcDeliveryMode.ReliableUnordered.ToSendOptions().DeliveryMethod);
        Assert.Equal(RudpDeliveryMethod.Sequenced, RpcDeliveryMode.Sequenced.ToSendOptions().DeliveryMethod);
        Assert.Equal(RudpDeliveryMethod.ReliableSequenced, RpcDeliveryMode.ReliableSequenced.ToSendOptions().DeliveryMethod);
        Assert.Equal(RudpDeliveryMethod.Unreliable, RpcDeliveryMode.Unreliable.ToSendOptions().DeliveryMethod);
        Assert.Equal(Enum.GetValues<RpcDeliveryMode>().Length, Enum.GetValues<RudpDeliveryMethod>().Length);
    }

    static T First<T>(FakeSession session) => (T)session.Sent.OfType<T>().First();

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The condition was not met within the expected time.");
    }
}

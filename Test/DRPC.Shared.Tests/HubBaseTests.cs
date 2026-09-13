using Communication.Network.RUDP;
using DRPC.Shared;
using DRPC.Shared.Message;
using DRPC.Shared.Network;
using Xunit;

namespace DRPC.Shared.Tests;

/// <summary>
/// HubBase 단위 테스트: CallId·타임아웃·동시성·오류 경로·전송 방식 배선. 네트워크 없이 FakeSession 으로 본다.
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
        // 진단 정확성 — 호출별 예산(50ms)으로 만료됐는데 허브 기본(30s)을 보고하면 운영자가 잘못된 노브를 의심한다.
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
        // 유휴 주차 — 대기표가 비면 1Hz 타이머를 반납하고, 다음 시간 제한 호출 시 재생성된다(피어 수만큼 틱이 쌓이는 서버 유휴 비용 절감).
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromMilliseconds(50) };

        var timerField = typeof(HubBase).GetField("_timeoutTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // 호출 시작과 동시에 타이머가 보호 중이다(EnsureTimeoutTimer 는 송신 await 전 동기 실행).
        Task<byte[]> pending = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        Assert.NotNull(timerField.GetValue(hub));
        await Assert.ThrowsAsync<TimeoutException>(() => pending);

        // 만료를 처리한 같은 틱이 스캔을 마치면 주차한다 — 수면 대신 폴링으로 기다린다(빠르고 플레이크 창이 좁다).
        await WaitUntilAsync(() => timerField.GetValue(hub) is null);

        // 재생성 — 다음 시간 제한 호출은 즉시 새 타이머의 보호를 받는다(만료 틱이 다시 같은 틱에 주차하므로 시작 직후에만 관측).
        Task<byte[]> second = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered);
        Assert.NotNull(timerField.GetValue(hub));
        await Assert.ThrowsAsync<TimeoutException>(() => second);
    }

    [Fact]
    public async Task TimeoutTimer_ParksWhenOnlyInfiniteBudgetCallsRemain()
    {
        // 무제한 예산(롱폴) 호출만 남은 피어도 주차한다 — deadline 0 은 스캔 대상이 아니어서 틱이 일만 한다.
        // 수정 전(대기표 비었을 때만 주차)에는 무제한 호출이 하나라도 남으면 1Hz 틱이 허브 수명 내내 유지됐다.
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromMilliseconds(50) };

        var timerField = typeof(HubBase).GetField("_timeoutTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Task<byte[]> timed = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered); // 타이머 보호 시작
        Task<byte[]> infinite = hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, Timeout.InfiniteTimeSpan);
        await Assert.ThrowsAsync<TimeoutException>(() => timed); // 시간 제한 대기 소멸 — 무제한만 남는다

        await WaitUntilAsync(() => timerField.GetValue(hub) is null); // 주차 — 수정 전엔 테이블이 비지 않아 영구 틱

        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(2u, Payload)); // 무제한 호출은 응답 수용
        Assert.Equal(Payload, await infinite);
    }

    [Fact]
    public void IsDisconnected_LatchesAfterDisconnectNotification()
    {
        // 구독 이전에 끊긴 허브는 Disconnected 이벤트를 받을 수 없다 — 리스너 회수용 관측 신호가 필요하다(RpcHost 즉시 회수).
        var session = new FakeSession();
        using var hub = new TestHub(session);

        Assert.False(hub.IsDisconnected);
        hub.NotifyDisconnected(new InvalidOperationException("test disconnect"));
        Assert.True(hub.IsDisconnected);
    }

    [Fact]
    public async Task Dispose_WhileRequestInFlight_DoesNotLeakUnobservedException()
    {
        // 서버 중지 정상 경로 — 처리 중 요청이 있어도 Dispose(세마포 해지)는 미관측 예외를 남기지 않아야 한다.
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

        // 테스트 이중(TestHub)으로 디스패치 태스크를 관측한다 — 방화벽이 없으면(수정 전) 해지 경쟁의 ObjectDisposedException 이 여기로 샌다.
        Task processing = hub.DispatchForTest(new ProcedureCallRequestMessage(1u, 1, Payload));
        await started.Task;
        hub.Dispose(); // 슬롯 점유 중 게이트 해지 — finally 의 Release 가 예외를 낼 수 있는 순간
        release.TrySetResult();

        await processing; // 방화벽이 정상 종료 경쟁을 흡수 — 디스패치 태스크는 무결하게 완료되어야 한다
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
        // 허브 기본 30초라도 호출별 50ms 예산이면 TimeoutException — 전역 상한을 희생하지 않고 이 호출만 빨리 끊는다.
        var session = new FakeSession();
        using var hub = new TestHub(session) { RpcTimeout = TimeSpan.FromSeconds(30) };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task RequestRPC_PerCallInfinite_OverridesFiniteHubDefault()
    {
        // 반대 방향: 허브 기본 50ms(≈1초 스캔 틱에 만료)라도 이 호출이 무제한을 고르면 대기 유지 — 뒤늦은 응답 수용.
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

        // 대기표 없는 CallId 의 지연·중복 응답은 다른 호출을 잘못된 값으로 완료시키면 안 된다.
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

        // one-way 신호는 CallId 0 (수신 측 등록표 아님)
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

        // 상한 초과 요청은 처리를 기다리지 않고 Overloaded 를 받는다.
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

        // 상한 도달 시 새 호출은 대기하지 않고 즉시 실패한다(fail-fast).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hub.RequestRPC(1, Payload, RpcDeliveryMode.ReliableOrdered));

        // 슬롯이 해제되면(응답 도착) 재시도할 수 있다.
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

        // 기본값(0=무제한)은 대기 테이블 크기와 무관하게 호출을 수용한다.
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

        Assert.Null(hub.LastDisconnectReason); // 끊김 전에는 null

        // 수신 경로(DRPCMessageHandler)가 세션 이벤트에서 호출하는 통지 — 사유를 남기고 이벤트를 숭긴다.
        hub.NotifyDisconnected(new InvalidOperationException("flow"),
            Communication.Shared.Connection.DisconnectReason.FlowControl);

        // Disconnected 핸들러 안에서 끊김 사유를 읽을 수 있다(형제 제안 P4 — FlowControl 백프레셔 식별).
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
        Assert.Contains("boom-42", error.Message); // 기본 동작 — 상세 전송(개발 편의)
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
        Assert.DoesNotContain("boom-42", error.Message); // 내부 상세는 원격으로 새지 않는다
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
        Assert.False(invoked); // 거부된 호출은 구현을 실행하지 않는다
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

        // 취소된 CallId 로 뒤늦은 응답이 도착해도 아무 완료를 만들지 않는다.
        hub.OnReceiveRPCResponseMessage(new ProcedureCallResponseMessage(1u, Payload));

        // 슬롯 반납 확인 — 상한 1에서도 새 호출이 수용된다.
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

        // 미등록 타입은 무시만 한다(수신 경로를 죽이지 않는다).
        handler.HandleMessage("not an rpc message");
        Assert.Single(session.Sent);
        GC.KeepAlive(handler);
    }

    [Fact]
    public void RpcDeliveryMap_CoversEveryMode()
    {
        // DRPC 계약 열거형과 RUDP 열거형의 대응이 끊기면 이 테스트가 잡는다.
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

        throw new TimeoutException("조건이 예상 시간 내에 충족되지 않았습니다.");
    }
}

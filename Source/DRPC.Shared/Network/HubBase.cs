using System.Collections.Concurrent;
using Communication.Shared.Connection;
using Communication.Shared.Sessions;
using DRPC.Shared.Interface;
using DRPC.Shared.Message;

namespace DRPC.Shared.Network;

/// <summary>
/// RPC 허브의 공용 런타임. <typeparamref name="TSPD"/>(서버 계약)와 <typeparamref name="TCPD"/>(클라이언트 계약)를
/// 매개변수로 받아 어느 쪽 엔드포인트든 같은 런타임을 쓴다.
/// </summary>
/// <typeparam name="TSPD">서버가 구현하는 함수 선언 인터페이스.</typeparam>
/// <typeparam name="TCPD">클라이언트가 구현하는 함수 선언 인터페이스.</typeparam>
public abstract class HubBase<TSPD, TCPD> : HubBase
    where TSPD : IServerProcedureDeclarations
    where TCPD : IClientProcedureDeclarations
{
    protected HubBase(Func<HubBase, ISession> sessionFactory)
        : base(sessionFactory)
    {
    }
}

/// <summary>
/// 왕복 RPC 런타임: outgoing 호출(CallId 할당·응답 대기·타임아웃), incoming 디스패치(동시 상한), 끊김 정리.
/// 생성된 허브 스텁만 이 클래스의 protected API 를 사용한다 — 사용자 코드는 직접 다루지 않는다.
/// </summary>
public abstract class HubBase : IHubBase, IDisposable
{
    readonly ISession _session;

    /// <summary>MethodId → 처리 위임(페이로드 바이트 → 응답 페이로드 바이트).</summary>
    protected Dictionary<int, Func<byte[], Task<byte[]>>> MethodCallActions { get; } = new();

    /// <summary>Incoming MethodId → 응답·오류 전송 시 사용할 전송 방식(요청의 방식과 일치시킨다).</summary>
    protected Dictionary<int, RpcDeliveryMode> MethodDeliveryModes { get; } = new();

    /// <summary>다음 CallId. 0 은 one-way 예약이라 절대 할당하지 않는다.</summary>
    int _nextCallId;

    sealed class PendingCall
    {
        public PendingCall(TaskCompletionSource<byte[]> tcs, long deadlineUtcTicks, TimeSpan effectiveTimeout)
        {
            Tcs = tcs;
            DeadlineUtcTicks = deadlineUtcTicks;
            EffectiveTimeout = effectiveTimeout;
        }

        public TaskCompletionSource<byte[]> Tcs { get; }

        /// <summary>0 이면 무제한(스캔 대상 아님).</summary>
        public long DeadlineUtcTicks { get; }

        /// <summary>이 호출에 실제 적용된 예산(허브 기본 또는 호출별 오버라이드) — 만료 보고 문구용.</summary>
        public TimeSpan EffectiveTimeout { get; }
    }

    readonly ConcurrentDictionary<uint, PendingCall> _pendingCalls = new();

    Timer? _timeoutTimer;
    readonly object _timeoutTimerGate = new();

    SemaphoreSlim? _incomingGate;
    int _maxConcurrentIncoming;
    readonly object _incomingGateLock = new();

    int _maxPendingCalls;

    /// <summary>
    /// <see cref="RpcErrorCode.Unhandled"/> 오류의 원격 응답에 예외 상세 메시지(<c>ex.Message</c>)를 실을지.
    /// 기본 <c>true</c>(기존 동작 — 신뢰 피어 간 개발 편의). <c>false</c> 면 내부 예외 문구(경로·내부 상태 노출 표면) 대신
    /// 고정 문구를 보낸다 — 인터넷 노출 엔드포인트 권장. 서버 측 <c>Trace</c> 기록은 설정과 무관하게 항상 남는다.
    /// </summary>
    public bool SendErrorDetails { get; set; } = true;

    int _disconnectRaised;
    bool _disposed;

    /// <summary>
    /// outgoing RPC 응답 대기 상한. 기본 30초. <see cref="Timeout.InfiniteTimeSpan"/> 또는 0 이하이면 무제한.
    /// 만료된 호출은 <see cref="TimeoutException"/> 으로 완료된다.
    /// </summary>
    /// <remarks>32비트 플랫폼(Unity IL2CPP 등)에서의 TimeSpan 티어 방지를 위해 ticks 를 volatile 로 읽고 쓴다.</remarks>
    public TimeSpan RpcTimeout
    {
        get => new TimeSpan(Volatile.Read(ref _rpcTimeoutTicks));
        set => Volatile.Write(ref _rpcTimeoutTicks, value.Ticks);
    }

    long _rpcTimeoutTicks = TimeSpan.FromSeconds(30).Ticks;

    /// <summary>
    /// 동시 Incoming 처리 상한. 0(기본)이면 무제한. 초과하면 non-one-way 요청은
    /// <see cref="RpcErrorCode.Overloaded"/> 오류를 받고 one-way 은 버려진다.
    /// </summary>
    /// <remarks>연결 직후·유휴 상태에서 설정한다. 처리 중인 실행과의 세마포어 재교대는 지원하지 않는다.</remarks>
    public int MaxConcurrentIncoming
    {
        get => _maxConcurrentIncoming;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            lock (_incomingGateLock)
            {
                _maxConcurrentIncoming = value;
                _incomingGate?.Dispose();
                _incomingGate = value > 0 ? new SemaphoreSlim(value, value) : null;
            }
        }
    }

    /// <summary>
    /// 동시 대기 중인 outgoing RPC(응답 대기 CallId) 상한. 0(기본)이면 무제한.
    /// 상한 도달 시 새 호출은 대기하지 않고 즉시 <see cref="InvalidOperationException"/> 으로 실패한다(fail-fast) —
    /// 응답 불능 피어에 대한 대기 테이블 무한 적체(메모리 고갈)를 끊는다.
    /// 검사·등록 사이 경쟁으로 순간적으로 상한을 약간 넘을 수 있다(근사 강제).
    /// </summary>
    public int MaxPendingCalls
    {
        get => _maxPendingCalls;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _maxPendingCalls = value;
        }
    }

    /// <summary>연결이 끊겼을 때 발생(세션당 1회). 대기 중 호출은 이미 실패 처리된 뒤다.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// 관측된 마지막 끊김 사유(형제 제안 P4 운영 신호 — <see cref="DisconnectReason.FlowControl"/> 백프레셔 식별 등).
    /// 끊김 전에는 <c>null</c>. <see cref="Disconnected"/> 핸들러 안에서 읽는다(이벤트 인자 확장 대신 — 시그니처 불변 유지).
    /// </summary>
    public DisconnectReason? LastDisconnectReason { get; private set; }

    /// <summary>
    /// <see cref="Disconnected"/> 이벤트가 이미 발화했는지(원격 끊김·<see cref="Disconnect()"/>·<see cref="Dispose"/> 완료).
    /// 이벤트는 허브 수명당 1회라 <b>구독 이전에 끊긴 허브는 이벤트를 받을 수 없다</b> — 리스너 측 즉시 회수,
    /// 늦은 구독자의 사전 검사용 관측 신호.
    /// </summary>
    public bool IsDisconnected => Volatile.Read(ref _disconnectRaised) != 0;

    protected HubBase(Func<HubBase, ISession> sessionFactory)
    {
        if (sessionFactory is null)
        {
            throw new ArgumentNullException(nameof(sessionFactory));
        }

        _session = sessionFactory.Invoke(this);
    }

    /// <summary>세션 끊김 관측 등을 위한 전송 진입점. 생성된 코드가 사용하는 protected 표면.</summary>
    protected internal ISession Session => _session;

    /// <summary>one-way 요청을 보낸다. CallId 는 0 고정(응답 대기표 없음).</summary>
    protected async Task SendRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode)
    {
        const uint callId = 0;
        var request = new ProcedureCallRequestMessage(callId, methodId, parameterData);
        await _session.SendAsync(request, mode.ToSendOptions()).ConfigureAwait(false);
    }

    /// <summary>
    /// 요청을 보내고 응답·오류·타임아웃·취소·끊김 중 하나로 완료되는 응답 바이트를 기다린다.
    /// <paramref name="cancellationToken"/> 이 취소되면 대기가 즉시 취소 완료되고 슬롯이 반납된다(이미 송신된 요청은 회수되지 않고, 뒤늦은 응답은 도착해도 무시된다).
    /// </summary>
    protected async Task<byte[]> RequestRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode,
        CancellationToken cancellationToken = default)
        => await RequestRPC(methodId, parameterData, mode, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 호출별 타임아웃 지정 버전. <paramref name="timeout"/> 이 null 이면 허브 기본(<see cref="RpcTimeout"/>)을 따르고,
    /// 값이 있으면 이 호출에만 그 예산이 적용된다(느린 배치 호출에만 넉넉한 상한을 주고 나머지는 허브 기본을 지키게 하는 용도).
    /// 해석은 허브 노브와 동일 — <see cref="Timeout.InfiniteTimeSpan"/> 또는 0 이하이면 이 호출은 무제한 대기한다.
    /// </summary>
    protected async Task<byte[]> RequestRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int pendingCap = Volatile.Read(ref _maxPendingCalls);
        if (pendingCap > 0 && _pendingCalls.Count >= pendingCap)
        {
            throw new InvalidOperationException(
                $"Outgoing RPC pending-call capacity ({pendingCap}) reached — the peer is not answering fast enough.");
        }

        uint callId = AllocateCallId();
        var waitResponse = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan effectiveTimeout = timeout ?? RpcTimeout;
        long deadline = ComputeDeadlineUtcTicks(effectiveTimeout);

        if (!_pendingCalls.TryAdd(callId, new PendingCall(waitResponse, deadline, effectiveTimeout)))
        {
            throw new InvalidOperationException($"The call id {callId} is already in use.");
        }

        CancellationTokenRegistration cancellation = default;
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                // 취소는 대기만 끝낸다 — 송신된 요청을 회수하지 않는다. 늦은 응답은 대기표가 없어 무시된다.
                cancellation = cancellationToken.Register(() =>
                {
                    if (_pendingCalls.TryRemove(callId, out var cancelled))
                    {
                        cancelled.Tcs.TrySetCanceled(cancellationToken);
                    }
                });
            }

            if (deadline > 0)
            {
                EnsureTimeoutTimer();
            }

            var request = new ProcedureCallRequestMessage(callId, methodId, parameterData);
            await _session.SendAsync(request, mode.ToSendOptions()).ConfigureAwait(false);
            return await waitResponse.Task.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
            _pendingCalls.TryRemove(callId, out _);
        }
    }

    static long ComputeDeadlineUtcTicks(TimeSpan effective)
    {
        if (effective == Timeout.InfiniteTimeSpan || effective <= TimeSpan.Zero)
        {
            return 0;
        }

        return DateTime.UtcNow.Add(effective).Ticks;
    }

    /// <summary>호버 공용 타이머 1개(1초 스캔). 호출당 CTS 는 만들지 않는다.</summary>
    void EnsureTimeoutTimer()
    {
        lock (_timeoutTimerGate)
        {
            if (_timeoutTimer != null || _disposed)
            {
                return;
            }

            _timeoutTimer = new Timer(static state => ((HubBase)state!).ScanTimeouts(), this,
                dueTime: 1000, period: 1000);
        }
    }

    void ScanTimeouts()
    {
        long now = DateTime.UtcNow.Ticks;
        foreach (var pair in _pendingCalls)
        {
            long deadline = pair.Value.DeadlineUtcTicks;
            if (deadline <= 0 || deadline > now)
            {
                continue;
            }

            if (_pendingCalls.TryRemove(pair.Key, out var pending))
            {
                pending.Tcs.TrySetException(
                    new TimeoutException($"RPC call {pair.Key} timed out after {pending.EffectiveTimeout}."));
            }
        }

        // 유휴 주차 — 시간 제한 대기(deadline > 0)가 하나도 없으면 타이머를 반납한다. 무제한(롱폴) 호출만
        // 남은 피어도 주차 대상이다(무제한은 스캔 대상이 아니어서 틱이 일만 한다). 역호출이라도 한 번 한 피어마다
        // 1Hz 틱이 서버 수명 내내 남는 유휴 비용과, Dispose 누락 시 타이머가 허브를 고정하는 누수를 함께 끊는다.
        // 조건은 게이트 안에서 재평가한다 — 스캔 루프의 낡은 값으로 판정하면 TryAdd 직후 EnsureTimeoutTimer 와의
        // 재생성 경쟁이 열린다(양쪽 다 같은 락을 쓰므로 게이트 안 판정은 주파를 직렬화한다).
        lock (_timeoutTimerGate)
        {
            if (_timeoutTimer is not null && !HasTimedPendingCall())
            {
                _timeoutTimer.Dispose();
                _timeoutTimer = null;
            }
        }
    }

    /// <summary>시간 제한 대기(deadline &gt; 0)가 하나라도 남았는가. 주차 판정용 — 게이트 안에서 호출해 경쟁을 직렬화한다.</summary>
    bool HasTimedPendingCall()
    {
        foreach (var pair in _pendingCalls)
        {
            if (pair.Value.DeadlineUtcTicks > 0)
            {
                return true;
            }
        }

        return false;
    }

    uint AllocateCallId()
    {
        uint id;
        do
        {
            id = (uint)Interlocked.Increment(ref _nextCallId);
        }
        while (id == 0);

        return id;
    }

    /// <summary>수신 요청을 처리 큐로 넘긴다(전송 콜백 스레드를 점유하지 않는다).</summary>
    public void OnReceiveRPCRequestMessage(ProcedureCallRequestMessage message)
    {
        _ = ProcessRequestAsync(message);
    }

    /// <summary>
    /// Fire-and-forget 방화벽 — 아래 코어를 벗어나는 예외는 전부 미관측 태스크 예외가 된다
    /// (서버 중지·세션 소멸과 경쟁하는 게이트 해지, 끊긴 세션으로의 Overloaded 오류 송신 등).
    /// 정상 종료 경로에서 나는 잔여 예외까지 잡아 Trace 로만 남긴다.
    /// protected 로 디스패치 태스크를 관측 가능하게 돌려준다(테스트 이중 관측용 — 생성 스텁도 사용하지 않는다, 사용자 코드 직접 호출 대상 아님).
    /// </summary>
    protected async Task ProcessRequestAsync(ProcedureCallRequestMessage message)
    {
        try
        {
            await ProcessRequestCoreAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                $"RPC request {message.MethodId} (call {message.CallId}) dispatch failed: {ex}");
        }
    }

    async Task ProcessRequestCoreAsync(ProcedureCallRequestMessage message)
    {
        bool oneWay = IsOneWay(message);

        SemaphoreSlim? gate;
        lock (_incomingGateLock)
        {
            gate = _incomingGate;
        }

        if (gate != null && !await gate.WaitAsync(0).ConfigureAwait(false))
        {
            if (!oneWay)
            {
                await SendErrorAsync(message.CallId, RpcErrorCode.Overloaded,
                    "Hub is at MaxConcurrentIncoming capacity.", ResolveMode(message.MethodId)).ConfigureAwait(false);
            }

            return;
        }

        try
        {
            // 호출 권한 검증 — lookup 전에 판정해 거부된 메서드의 존재 여부도 노출하지 않는다.
            if (!await AuthorizeRequestAsync(message.MethodId).ConfigureAwait(false))
            {
                if (!oneWay)
                {
                    await SendErrorAsync(message.CallId, RpcErrorCode.PermissionDenied,
                        $"The call to method {message.MethodId} is not authorized for this peer.",
                        ResolveMode(message.MethodId)).ConfigureAwait(false);
                }

                return;
            }

            if (!MethodCallActions.TryGetValue(message.MethodId, out Func<byte[], Task<byte[]>>? action) || action is null)
            {
                if (!oneWay)
                {
                    await SendErrorAsync(message.CallId, RpcErrorCode.UnknownMethod,
                        $"The method {message.MethodId} does not exist.", ResolveMode(message.MethodId)).ConfigureAwait(false);
                }

                return;
            }

            RpcDeliveryMode mode = ResolveMode(message.MethodId);
            byte[] result = await action(message.ParameterData).ConfigureAwait(false);

            if (!oneWay)
            {
                await _session.SendAsync(new ProcedureCallResponseMessage(message.CallId, result), mode.ToSendOptions())
                    .ConfigureAwait(false);
            }
        }
        catch (RpcValidationFailedException ex)
        {
            // 검증 거부는 예상된 결과(미구현 버그 아님) — 오류 응답만 한다. one-way 는 응답 채널이 없어 스킵.
            if (!oneWay)
            {
                await SendErrorAsync(message.CallId, RpcErrorCode.ValidationFailed, ex.Message, ResolveMode(message.MethodId)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // 서버 측 관측 — 원격 전송 여부·상세 여부와 무관하게 항상 기록한다(콘솔 의존 금지 — Trace 만).
            System.Diagnostics.Trace.TraceError($"RPC method {message.MethodId} (call {message.CallId}) unhandled: {ex}");

            if (oneWay)
            {
                return;
            }

            try
            {
                string detail = SendErrorDetails
                    ? ex.Message
                    : "An unhandled error occurred on the remote hub.";
                await SendErrorAsync(message.CallId, RpcErrorCode.Unhandled, detail, ResolveMode(message.MethodId))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 오류 보고 실패는 원래 실패를 가리지 않는다.
            }
        }
        finally
        {
            try
            {
                gate?.Release();
            }
            catch (ObjectDisposedException)
            {
                // 허브 Dispose·MaxConcurrentIncoming 재설정이 슬롯 점유 중 게이트를 해지했다 — 정상 종료 경쟁.
            }
        }
    }

    /// <summary>
    /// 와이어상의 one-way 신호는 CallId 0 고정이다. <see cref="SendRPC"/> 는 0 을 보내고
    /// <see cref="RequestRPC"/> 는 1 부터 할당하므로 두 값은 겹치지 않는다.
    /// 수신 측 등록표로 판정하지 않는다 — 서버·클라이언트 계약이 같은 MethodId 를 서로 다르게 one-way 로
    /// 선언해도 어긋나지 않고, 미등록 MethodId 도 올바르게 취급된다.
    /// </summary>
    static bool IsOneWay(ProcedureCallRequestMessage message) => message.CallId == 0;

    /// <summary>요청 MethodId 에 등록된 전송 방식. 미등록이면 ReliableOrdered.</summary>
    RpcDeliveryMode ResolveMode(int methodId)
        => MethodDeliveryModes.TryGetValue(methodId, out var mode) ? mode : RpcDeliveryMode.ReliableOrdered;

    /// <summary>
    /// Incoming RPC 호출 권한 검증 훅. 기본은 전부 허용(true).
    /// 서버 허브에서 override 해 메서드별 호출 권한(예: 관리자 전용 프로시저)을 검사한다.
    /// 거부 시 non-one-way 호출은 <see cref="RpcErrorCode.PermissionDenied"/> 오류를 받고 one-way 는 폐기된다.
    /// 메서드 등록표 조회보다 먼저 판정하므로 미등록 MethodId 의 존재 여부도 노출하지 않는다.
    /// 처리 동시 상한(MaxConcurrentIncoming) 슬롯 안에서 호출됨 — 긴 검사는 상한 소진에 유의.
    /// </summary>
    protected virtual Task<bool> AuthorizeRequestAsync(int methodId) => Task.FromResult(true);

    Task SendErrorAsync(uint callId, int errorCode, string message, RpcDeliveryMode mode)
        => _session.SendAsync(new ProcedureCallErrorMessage(callId, errorCode, message), mode.ToSendOptions());

    /// <summary>응답 바이트를 대기 중인 호출에 전달. 대기표가 없는 응답(지연 도착·중복)은 버린다.</summary>
    public void OnReceiveRPCResponseMessage(ProcedureCallResponseMessage message)
    {
        if (_pendingCalls.TryRemove(message.CallId, out var pending))
        {
            pending.Tcs.TrySetResult(message.ReturnData);
        }
    }

    /// <summary>오류 응답을 <see cref="RpcFaultException"/> 으로 대기 중인 호출에 전달한다.</summary>
    public void OnReceiveRPCErrorMessage(ProcedureCallErrorMessage message)
    {
        if (_pendingCalls.TryRemove(message.CallId, out var pending))
        {
            pending.Tcs.TrySetException(new RpcFaultException(message.CallId, message.ErrorCode, message.Message));
        }
    }

    public void CancelPendingCalls(Exception reason)
    {
        foreach (var pair in _pendingCalls)
        {
            if (_pendingCalls.TryRemove(pair.Key, out var pending))
            {
                pending.Tcs.TrySetException(reason);
            }
        }
    }

    /// <summary>대기 중 RPC를 실패시키고 세션을 끊은 뒤 <see cref="Disconnected"/> 를 발생시킨다.</summary>
    public void Disconnect()
    {
        CancelPendingCalls(new InvalidOperationException("RPC session disconnected."));

        try
        {
            _session.Disconnect();
        }
        catch
        {
            // 끊김 정리 중 예외는 호출자에게 전파하지 않는다.
        }

        RaiseDisconnected();
    }

    /// <summary>수신 경로(세션 이벤트)에서 끊김을 통지할 때 사용한다.</summary>
    public void NotifyDisconnected(Exception? reason)
        => NotifyDisconnected(reason, null);

    /// <summary>끊김 사유를 함께 남기고 통지한다(사유 없으면 <see cref="LastDisconnectReason"/> 은 이전값 유지).</summary>
    public void NotifyDisconnected(Exception? reason, DisconnectReason? disconnectReason)
    {
        if (disconnectReason is { } observed)
        {
            LastDisconnectReason = observed;
        }

        CancelPendingCalls(reason ?? new InvalidOperationException("RPC session disconnected."));
        RaiseDisconnected();
    }

    void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
        {
            return;
        }

        try
        {
            Disconnected?.Invoke();
        }
        catch
        {
            // 구독자 예외는 런타임을 죽이지 않는다.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_timeoutTimerGate)
        {
            _timeoutTimer?.Dispose();
            _timeoutTimer = null;
        }

        Disconnect();

        lock (_incomingGateLock)
        {
            _incomingGate?.Dispose();
            _incomingGate = null;
        }
    }
}

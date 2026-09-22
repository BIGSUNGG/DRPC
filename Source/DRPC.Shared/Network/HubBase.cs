using System.Collections.Concurrent;
using Communication.Shared.Connection;
using Communication.Shared.Sessions;
using DRPC.Shared.Interface;
using DRPC.Shared.Message;

namespace DRPC.Shared.Network;

/// <summary>
/// Shared runtime of the RPC hub. Parameterized by <typeparamref name="TSPD"/> (server contract) and
/// <typeparamref name="TCPD"/> (client contract) so both endpoint kinds reuse the same runtime.
/// </summary>
/// <typeparam name="TSPD">The procedure-declaration interface the server implements.</typeparam>
/// <typeparam name="TCPD">The procedure-declaration interface the client implements.</typeparam>
public abstract class HubBase<TSPD, TCPD> : HubBase
    where TSPD : IServerProcedureDeclarations
    where TCPD : IClientProcedureDeclarations
{
    /// <summary>Initializes the hub with the session factory.</summary>
    protected HubBase(Func<HubBase, ISession> sessionFactory)
        : base(sessionFactory)
    {
    }
}

/// <summary>
/// The round-trip RPC runtime: outgoing calls (CallId allocation, response wait, timeouts), incoming
/// dispatch (concurrency cap), and disconnection cleanup. Only the generated hub stubs use this class's
/// protected API — user code does not touch it directly.
/// </summary>
public abstract class HubBase : IHubBase, IDisposable
{
    readonly ISession _session;

    /// <summary>MethodId → handler delegate (payload bytes → response payload bytes).</summary>
    protected Dictionary<int, Func<byte[], Task<byte[]>>> MethodCallActions { get; } = new();

    /// <summary>Incoming MethodId → delivery mode used when sending the response or error (matched to the request's mode).</summary>
    protected Dictionary<int, RpcDeliveryMode> MethodDeliveryModes { get; } = new();

    /// <summary>Next CallId. 0 is reserved for one-way and is never allocated.</summary>
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

        /// <summary>0 means unlimited (excluded from timeout scans).</summary>
        public long DeadlineUtcTicks { get; }

        /// <summary>The budget actually applied to this call (hub default or per-call override) — used in the expiry message.</summary>
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
    /// Whether the remote response for <see cref="RpcErrorCode.Unhandled"/> errors carries the exception
    /// detail (<c>ex.Message</c>). Default <c>true</c> (existing behavior — convenient when developing
    /// between trusted peers). When <c>false</c>, a fixed message is sent instead of the internal exception
    /// text (which can leak paths and internal state) — recommended for internet-exposed endpoints. The
    /// server-side <c>Trace</c> log is always written regardless of this setting.
    /// </summary>
    public bool SendErrorDetails { get; set; } = true;

    int _disconnectRaised;
    bool _disposed;

    /// <summary>
    /// The maximum wait for an outgoing RPC response. Default 30 seconds. <see cref="Timeout.InfiniteTimeSpan"/>
    /// or a value of 0 or less means unlimited. Expired calls complete with <see cref="TimeoutException"/>.
    /// </summary>
    /// <remarks>Ticks are read and written as volatile to prevent TimeSpan tearing on 32-bit platforms (Unity IL2CPP).</remarks>
    public TimeSpan RpcTimeout
    {
        get => new TimeSpan(Volatile.Read(ref _rpcTimeoutTicks));
        set => Volatile.Write(ref _rpcTimeoutTicks, value.Ticks);
    }

    long _rpcTimeoutTicks = TimeSpan.FromSeconds(30).Ticks;

    /// <summary>
    /// The cap on concurrent incoming processing. 0 (default) means unlimited. When exceeded, non-one-way
    /// requests receive a <see cref="RpcErrorCode.Overloaded"/> error and one-way requests are dropped.
    /// </summary>
    /// <remarks>Set right after connecting or while idle. Re-arming the semaphore against in-flight executions is not supported.</remarks>
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
    /// The cap on concurrently pending outgoing RPCs (CallIds awaiting a response). 0 (default) means unlimited.
    /// At the cap, a new call fails immediately with <see cref="InvalidOperationException"/> instead of queueing
    /// (fail-fast) — it cuts unbounded growth of the wait table (memory exhaustion) against a peer that never
    /// answers. A momentary overshoot slightly beyond the cap is possible due to the check-then-register race
    /// (approximate enforcement).
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

    /// <summary>Raised when the connection ends (once per hub). Pending calls have already been failed by the time this runs.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// The last observed disconnection cause (sibling proposal P4 operational signal — identifies
    /// <see cref="DisconnectReason.FlowControl"/> backpressure, etc.). <c>null</c> before disconnection.
    /// Read it inside a <see cref="Disconnected"/> handler (instead of extending event args — keeps the
    /// signature stable).
    /// </summary>
    public DisconnectReason? LastDisconnectReason { get; private set; }

    /// <summary>
    /// Whether the <see cref="Disconnected"/> event has already fired (remote disconnect, <see cref="Disconnect()"/>,
    /// or <see cref="Dispose"/> completed). The event fires once per hub lifetime, so <b>a hub that disconnected
    /// before you subscribed cannot deliver the event</b> — an observable signal for immediate reclamation on
    /// listener sides and for late subscribers to check first.
    /// </summary>
    public bool IsDisconnected => Volatile.Read(ref _disconnectRaised) != 0;

    /// <summary>Initializes the hub by opening its session through the factory.</summary>
    protected HubBase(Func<HubBase, ISession> sessionFactory)
    {
        if (sessionFactory is null)
        {
            throw new ArgumentNullException(nameof(sessionFactory));
        }

        _session = sessionFactory.Invoke(this);
    }

    /// <summary>The transport entry point, also used to observe session events. Protected surface used by generated code.</summary>
    protected internal ISession Session => _session;

    /// <summary>Sends a one-way request. CallId is fixed at 0 (no wait-table entry).</summary>
    protected async Task SendRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode)
    {
        const uint callId = 0;
        var request = new ProcedureCallRequestMessage(callId, methodId, parameterData);
        await _session.SendAsync(request, mode.ToSendOptions()).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and waits for the response bytes, completing on response, error, timeout,
    /// cancellation, or disconnection. If <paramref name="cancellationToken"/> is cancelled, the wait
    /// completes as cancelled and the slot is released (the already-sent request is not recalled; a late
    /// response is ignored because no wait entry remains).
    /// </summary>
    protected async Task<byte[]> RequestRPC(int methodId, byte[] parameterData, RpcDeliveryMode mode,
        CancellationToken cancellationToken = default)
        => await RequestRPC(methodId, parameterData, mode, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Per-call timeout variant. When <paramref name="timeout"/> is null, the hub default
    /// (<see cref="RpcTimeout"/>) applies; a value applies that budget to this call only (gives slow
    /// batch calls a generous limit while everything else keeps the hub default). Interpretation matches
    /// the hub knob — <see cref="Timeout.InfiniteTimeSpan"/> or 0 or less waits indefinitely for this call.
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
                // Cancellation only ends the wait — it does not recall the sent request. Late responses hit no wait entry and are ignored.
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

    /// <summary>One shared scan timer (1s tick). No per-call CTS is ever created.</summary>
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

        // Idle parking — when no deadline-bounded wait (deadline > 0) remains, return the timer. Peers with
        // only unlimited (long-poll) calls also park (unlimited calls are not scanned, so ticks would be pure
        // overhead). This kills both the idle cost of a 1 Hz tick lingering for the server's lifetime after a
        // single reverse call, and the leak where a missed Dispose pins the hub via the timer. The condition is
        // re-evaluated inside the gate — judging from the stale scan-loop value would open a re-creation race
        // with EnsureTimeoutTimer right after a TryAdd (both take the same lock, so the in-gate decision
        // serializes the handoff).
        lock (_timeoutTimerGate)
        {
            if (_timeoutTimer is not null && !HasTimedPendingCall())
            {
                _timeoutTimer.Dispose();
                _timeoutTimer = null;
            }
        }
    }

    /// <summary>Whether at least one deadline-bounded wait (deadline &gt; 0) remains. Parking decision — called inside the gate to serialize against races.</summary>
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

    /// <summary>Hands a received request to the processing queue (does not occupy the transport callback thread).</summary>
    public void OnReceiveRPCRequestMessage(ProcedureCallRequestMessage message)
    {
        _ = ProcessRequestAsync(message);
    }

    /// <summary>
    /// Fire-and-forget firewall — any exception escaping the core below becomes an unobserved task
    /// exception (gate disposal racing server shutdown or session teardown, Overloaded error sends onto
    /// a dead session, etc.). It also catches residual exceptions on the normal shutdown path, leaving
    /// only a Trace entry. Exposes the dispatch task as observable via protected (for dual observation in
    /// tests — generated stubs do not use it either; not intended for direct user calls).
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
            // Authorization — decided before the method lookup so even the existence of a rejected method is not revealed.
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
            // Validation rejection is an expected outcome (not an implementation bug) — error response only. One-way has no response channel, so it is skipped.
            if (!oneWay)
            {
                await SendErrorAsync(message.CallId, RpcErrorCode.ValidationFailed, ex.Message, ResolveMode(message.MethodId)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Server-side observation — always logged regardless of whether/how it is sent remotely (no console dependency — Trace only).
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
                // A failure to report the error must not mask the original failure.
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
                // Hub Dispose or a MaxConcurrentIncoming reset disposed the gate while a slot was held — normal shutdown race.
            }
        }
    }

    /// <summary>
    /// The wire signal for one-way is a fixed CallId of 0. SendRPC sends 0 and
    /// RequestRPC allocates from 1, so the two ranges never overlap. It is not decided from
    /// the receive-side registration table — server and client contracts may declare the same MethodId
    /// differently as one-way without mismatch, and unregistered MethodIds are still handled correctly.
    /// </summary>
    static bool IsOneWay(ProcedureCallRequestMessage message) => message.CallId == 0;

    /// <summary>The delivery mode registered for the request's MethodId. ReliableOrdered when unregistered.</summary>
    RpcDeliveryMode ResolveMode(int methodId)
        => MethodDeliveryModes.TryGetValue(methodId, out var mode) ? mode : RpcDeliveryMode.ReliableOrdered;

    /// <summary>
    /// Hook for authorizing incoming RPC calls. The default allows everything (true).
    /// Override in a server hub to check per-method call permission (e.g. admin-only procedures).
    /// On denial, non-one-way calls receive a <see cref="RpcErrorCode.PermissionDenied"/> error and one-way
    /// calls are discarded. It is decided before the method registration lookup, so the existence of an
    /// unregistered MethodId is not revealed either. Runs inside a MaxConcurrentIncoming slot — beware that
    /// long checks consume the concurrency budget.
    /// </summary>
    protected virtual Task<bool> AuthorizeRequestAsync(int methodId) => Task.FromResult(true);

    Task SendErrorAsync(uint callId, int errorCode, string message, RpcDeliveryMode mode)
        => _session.SendAsync(new ProcedureCallErrorMessage(callId, errorCode, message), mode.ToSendOptions());

    /// <summary>Delivers response bytes to the waiting call. Responses with no wait entry (late or duplicate) are discarded.</summary>
    public void OnReceiveRPCResponseMessage(ProcedureCallResponseMessage message)
    {
        if (_pendingCalls.TryRemove(message.CallId, out var pending))
        {
            pending.Tcs.TrySetResult(message.ReturnData);
        }
    }

    /// <summary>Delivers an error response to the waiting call as a <see cref="RpcFaultException"/>.</summary>
    public void OnReceiveRPCErrorMessage(ProcedureCallErrorMessage message)
    {
        if (_pendingCalls.TryRemove(message.CallId, out var pending))
        {
            pending.Tcs.TrySetException(new RpcFaultException(message.CallId, message.ErrorCode, message.Message));
        }
    }

    /// <summary>Fails every pending outgoing RPC with <paramref name="reason"/>.</summary>
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

    /// <summary>Fails pending RPCs and then raises <see cref="Disconnected"/> after ending the session.</summary>
    public void Disconnect()
    {
        CancelPendingCalls(new InvalidOperationException("RPC session disconnected."));

        try
        {
            _session.Disconnect();
        }
        catch
        {
            // Exceptions during disconnect cleanup must not propagate to the caller.
        }

        RaiseDisconnected();
    }

    /// <summary>Used by the inbound path (session events) to report a disconnection.</summary>
    public void NotifyDisconnected(Exception? reason)
        => NotifyDisconnected(reason, null);

    /// <summary>Notifies with the disconnection cause recorded (when absent, <see cref="LastDisconnectReason"/> keeps its previous value).</summary>
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
            // Subscriber exceptions must not kill the runtime.
        }
    }

    /// <summary>Fails pending calls, disconnects the session, and releases the timer and incoming gate.</summary>
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

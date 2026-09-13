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
/// 실제 RUDP(127.0.0.1) 왕복 검증. 전송 방식·OneWay·역호출·오류 경로를 와이어 위에서 확인한다.
/// 테스트마다 다른 포트를 써서 병렬 실행을 안전하게 한다.
/// </summary>
public class RudpLoopbackTests
{
    const string Key = "e2e-key";

    static int NextPort()
    {
        // OS 가 배정한 임시 포트를 쓴다 — 고정 시드(9600…)는 Windows 예약 포트 범위·선행 실행 잔여 리스너와 충돌해
        // "리스너 바인딩 실패" 플레이크를 일으켰다. 확보-해제-재바인드 사이 미세 경쟁은 감수한다.
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

        // void + non-OneWay 이라 빈 응답까지 왕복한다. Unreliable 은 루프백에서 유실되지 않는다.
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
        // 아무도 듣지 않는 포트(블랙홄): 임시 포트를 확보한 뒤 닫아 만든다.
        int silentPort;
        using (var probe = new System.Net.Sockets.UdpClient(0))
        {
            silentPort = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 300ms 상한: LiteNetLib 기본(약 5초)이 아니라 그 이내로 실패가 확정돼야 한다.
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

        // 서버 구현은 2초 지연(Slow) — 토큰 예산(300ms)이 대기를 먼저 끊는다(와이어 위 취소 검증).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Within(client.SlowAsync(2000, cts.Token), 5000));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"cancellation took {stopwatch.Elapsed} — token budget did not bound the wait");

        // 취소는 세션을 오염시키지 않는다 — 같은 연결로 후속 호출이 정상 왕복한다.
        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
    }

    [Fact]
    public async Task Per_call_timeout_ends_wait_and_session_stays_usable()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);

        // 서버 구현은 3초 지연 — 호출별 예산 400ms(타임아웃 스캔 틱 1초 포함 ≲2초)가 허브 기본(30초) 대신 대기를 끊는다.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => Within(client.SlowWithPerCallTimeoutAsync(3000), 5000));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"per-call timeout took {stopwatch.Elapsed} — budget did not bound the wait");

        // 만료는 세션을 오염시키지 않는다 — 같은 연결로 후속 호출이 정상 왕복한다(뒤늦은 응답은 버려진다).
        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
    }

    [Fact]
    public async Task Listener_max_connections_rejects_excess_peer()
    {
        int port = NextPort();

        // 상한 1: 연결 고갈 공격 방어 — 초과 접속은 즉시 거부되고 수락은 계속된다.
        await using var handle = await RpcHost.ListenAsync(port, 1, Key,
            channel => new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)),
            _ => Task.CompletedTask);

        using var first = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        Assert.Equal(5, await Within(first.AddAsync(2, 3))); // 상한 이내 클라는 정상 동작

        // 상한 초과 — 재시도 소진이 아니라 즉시 거부 통보로 ConnectAsync 가 실패한다.
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

        // 서버·클라 모두 CRC32c 켠 설정 — 무결성 레이어 위에서 정상 왕복한다.
        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, EnableCrc32c = true };
        await using var handle = await RpcHost.ListenWithOptionsAsync(port, serverOptions,
            channel => new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)),
            _ => Task.CompletedTask);

        var clientOptions = new RpcEndpointOptions { ConnectionKey = Key, EnableCrc32c = true, ConnectTimeoutMs = 3000 };
        using var client = await RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
        client.Dispose();

        // 와이어 비호환 확인 — CRC 를 끈 클라이언트는 체크섬 위반 패킷으로 취급돼 연결 수립 불가(300ms 상한으로 빠르게 실패).
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

        Assert.Equal(0, handle.ActiveConnectionCount); // 수락 전

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);
        await Within(client.AddAsync(2, 3));
        await WaitUntilAsync(() => handle.ActiveConnectionCount == 1);

        client.Dispose();
        await WaitUntilAsync(() => handle.ActiveConnectionCount == 0); // 끊김 회수(형제 제안 P4 운영 신호)
    }

    [Fact]
    public async Task Peer_dead_before_subscription_is_swept_from_active_count()
    {
        int port = NextPort();

        // 수용→구독 창구에 이미 죽은 허브 — 끊김 이벤트(수명당 1회)는 구독자 없이 발화를 마쳤다.
        // 스윕이 없으면(수정 전) 이 peer 는 Stop 까지 ActiveConnectionCount 를 1로 유지한다.
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handle = await RpcHost.ListenAsync(port, Key, channel =>
        {
            var hub = new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub));
            hub.NotifyDisconnected(new InvalidOperationException("dead on arrival"));
            return hub;
        }, _ => { accepted.TrySetResult(); return Task.CompletedTask; });

        using var client = await E2EClientHub.ConnectAsync("127.0.0.1", port, Key);

        await accepted.Task; // 수용(→스윕) 완료 관측 — 이 시점 카운터가 0이어야 한다
        await WaitUntilAsync(() => handle.ActiveConnectionCount == 0); // 대기 없이 즉시 회수
    }

    [Fact]
    public async Task Queue_options_flow_through_session_factory()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        // 세션 팩토리에 큐 옵션 통과 — 송신 프레임 상한 64바이트로 좁힌다(형제 제안 P3: FrameTimeout·MaxFrameLength 통합 관리).
        var queueOptions = new Communication.Shared.Messages.MessageQueueOptions { MaxFrameLength = 64 };
        using var client = await RpcClient.ConnectAsync("127.0.0.1", port, Key,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub, queueOptions)));
        client.RpcTimeout = TimeSpan.FromSeconds(2);

        // 상한 이내 프레임은 정상 왕복한다.
        Assert.Equal(5, await Within(client.AddAsync(2, 3)));

        // 상한 초과 페이로드는 송신 격리되어 응답 없음 — 타임아웃(또는 격리 예외)으로 2초 이내 실패한다.
        string big = new string('x', 200);
        await Assert.ThrowsAnyAsync<Exception>(() => Within(client.EchoAsync(big)));
    }

    [Fact]
    public async Task Unknown_method_id_yields_unknown_method_fault()
    {
        int port = NextPort();
        await using var handle = await E2EServerHub.ListenAsync(port, Key, _ => Task.CompletedTask);

        // 서버가 모르는 MethodId 를 직접 심는다(생성 스텁을 우회하는 원시 요청).
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

        // 타입 인자 없이 일반 호출처럼 쓴다 — T 가 인자에서 추론된다.
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
        // T3=ChatLine 로 보낸 파생 타입이 그대로 도착한다(그룹 다형성 보존).
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

        // 생성 스텁을 우회해 선언 밖 구성 인덱스를 실어 보낸다(런타임 백스톱 검증).
        using var rogue = await RpcClient.ConnectAsync("127.0.0.1", port, Key,
            channel => new RogueClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        // GetDefault(methodId 7) 의 페이로드 = 구성 인덱스 int32 하나.
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

        // GuardedReject_Implementation 은 호출되면 예외를 던지므로, 코드 7 이 오는 것이 곧 미호출 증명이다.
        RpcFaultException fault = await Assert.ThrowsAsync<RpcFaultException>(() => Within(client.GuardedRejectAsync(1)));
        Assert.Equal(RpcErrorCode.ValidationFailed, fault.ErrorCode);
        client.Dispose();
    }

    [Fact]
    public async Task Generic_validation_gates_implementation()
    {
        var (_, client, handle) = await PairAsync();
        await using var _ = handle;

        // T=int 통과(기본값 0 왕복), T=string 은 _Validate false → ValidationFailed(7).
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
            throw new TimeoutException($"주어진 시간({timeoutMs}ms) 내에 완료되지 않았습니다.");
        }

        return await task.ConfigureAwait(false);
    }

    static async Task Within(Task task, int timeoutMs = 8000)
    {
        if (await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException($"주어진 시간({timeoutMs}ms) 내에 완료되지 않았습니다.");
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

        throw new TimeoutException("주어진 시간 내에 조건이 충족되지 않았습니다.");
    }
}

/// <summary>
/// 생성 스텁 없이 원시 요청을 보내는 클라이언트(미등록 MethodId 경로를 검증하기 위한 용도).
/// 계약 자체를 비워 두어 스텁 생성을 없애고, 허브 자체는 partial 이어야 DRPCGEN001 이 나지 않는다.
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

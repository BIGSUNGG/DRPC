using System.Collections.Concurrent;
using Communication.Network.RUDP;
using Communication.Shared.Channels;
using DRPC.Shared.Network;

namespace DRPC.Server.Network;

/// <summary>
/// RUDP 리스너 수명. 생성된 <c>{Hub}.ListenAsync</c> 가 이 메서드를 부른다.
/// peer 마다 허브를 1개 만들고, 중지 시 리스너와 peer 허브를 함께 정리한다.
/// </summary>
public static class RpcHost
{
    /// <exception cref="InvalidOperationException">바인딩 실패·이미 시작된 리스너.</exception>
    public static Task<RpcListenHandle> ListenAsync<THub>(
        int port,
        string? connectionKey,
        Func<IMessageChannel, THub> hubFactory,
        Func<THub, Task>? onConnected = null,
        CancellationToken cancellationToken = default)
        where THub : Shared.Network.HubBase
        => ListenAsync(port, 0, connectionKey, hubFactory, onConnected, cancellationToken);

    /// <summary>
    /// <paramref name="maxConnections"/> 가 양수면 동시 수락 연결 수 상한(연결 고갈 공격 방어 — 상한 도달 시
    /// 접속 요청은 즉시 거부되고 수락은 계속된다). 0이면(기본) 무제한.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConnections"/> 가 음수.</exception>
    /// <exception cref="InvalidOperationException">바인딩 실패·이미 시작된 리스너.</exception>
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
    /// <see cref="RpcEndpointOptions"/> 로 전송 옵션(키·연결 상한·CRC32c 무결성 등)을 일괄 지정한다.
    /// CRC32c 는 양단 모두 같은 설정이어야 한다(와이어 비호환).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="endpointOptions"/> 가 null.</exception>
    /// <exception cref="InvalidOperationException">바인딩 실퍽·이미 시작된 리스너.</exception>
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

            // 수용→구독 창구에 단절된 피어(세션 생성 중 끊김)는 끊김 이벤트를 못 받는다(허브 수명당 1회 발화) —
            // 즉시 회수하지 않으면 Stop 까지 peers 에 남아 ActiveConnectionCount 를 부풀린다.
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
                    // 접속 콜백 예외는 수신 경로를 죽이지 않는다(콘솔 의존 금지 — Trace 로만 남긴다).
                    System.Diagnostics.Trace.TraceError($"onConnected 예외: {e}");
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

        // 취소로도 중지가 관찰돼야 한다(ListenTask 가 영구 미완료로 남지 않도록).
        linkedCts.Token.Register(static state => ((Action)state!).Invoke(), new Action(Stop), useSynchronizationContext: false);

        return Task.FromResult(handle);
    }
}

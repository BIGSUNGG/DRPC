---
project: DS_RPC
type: guide
status: stable
tags: [guide, production, hardening, operations]
updated: 2026-09-13
---

# Production-Hardening — 상용 투입 런북

게임 서버를 인터넷에 내놓기 전에 켤 것·정할 것을 한 곳에 정리한다. 모든 예시는 v2.9.1 실제 API.

## 1. 접속 예산 — 침묵 호스트에 낭비 없애기

```csharp
// 클라: 연결 실패를 기본(약 5초) 대신 800ms 안에 확정
using var client = await GameClientHub.ConnectAsync("game.example.com", 9050, key);            // 기본 경로
using var fast = await RpcClient.ConnectAsync(host, port, key, 800,
    channel => new GameClientHub(h => HubSessionFactory.CreateRudpSession(channel, h)));       // 상한 지정
```

끝점 단위 묶음(`RpcEndpointOptions`)은 키·타임아웃·상한·CRC 를 한 객체로:

```csharp
var options = new RpcEndpointOptions
{
    ConnectionKey = "server-only-key",
    ConnectTimeoutMs = 800,
    EnableCrc32c = true,          // §3 참고 — 양단 일치 필수
};
```

## 2. 서버 상한 — 고갈 공격 방어 3종

| 계층 | 노브 | 기본 | 도달 시 동작 |
| ------ | ------ | ------ | --------------- |
| 수락 연결 | `RpcHost.ListenAsync(port, maxConnections, …)` | 0(무제한) | 초과 접속 **즉시 거부**, 수락 계속 |
| 수신 동시성 | `hub.MaxConcurrentIncoming = 32` | 0(무제한) | `Overloaded` 오류 응답(one-way 는 폐기) |
| 송신 대기 | `hub.MaxPendingCalls = 256` | 0(무제한) | 새 호출 fail-fast `InvalidOperationException` |

세 노브 모두 기본 무제한(하위호환) — 상용 배포 시 **명시적 상한 설정 필수**.

## 3. 패킷 무결성 — CRC32c (옵트인)

```csharp
options.EnableCrc32c = true;   // 서버·클라 양단 같은 값 (와이어 비호환 — 한쪽만 켜면 통신 불가)
```

송신마다 체크섬(4B)을 붙이고 수신은 위반 패킷을 **프로토콜 처리 전에 폐기** — IPv4 UDP 체크섬 비활성(0) 경로 차단. 위변조 **검출** 전용(키 없는 CRC — 기밀성·인증 없음).

## 3.5. 패킷 암호화 — DTLS 1.2 (옵트인, v2.11.0+)

```csharp
// 서버: 인증서 지정 — 이 순간 모든 패킷이 DTLS 1.2 로 암호화된다(양단 모두 켜야 함 — 와이어 비호환)
var serverOptions = new RpcEndpointOptions { ServerCertificate = serverCert };

// 클라: 핀닝(권장 — 게임 표준) 또는 TargetHost 검증. 둘 다 없으면 서버 인증서 기본 거부(fail-closed)
var clientOptions = new RpcEndpointOptions
{
    TlsCertificateValidation = der => RudpTlsOptions.GetSha256Fingerprint(der) == expectedFingerprint,
};
```

기밀성 + 서버 인증(클라 관점). ConnectionKey 평문 비교·클라이언트 인증은 그대로 앱 계층 몫(세션 토큰 + `AuthorizeRequestAsync`).
자세한 계약: [[../01-Overview/Feature-Spec|Feature-Spec]] F13, [[../05-Decisions/0003-dtls-delegation-and-flat-options|ADR-0003]].

## 4. 호출 권한 — 관리자 전용 프로시저

```csharp
public sealed class GameServerHub : ServerHub<IGameProcedures, IGameClientProcedures>
{
    private partial Task Shutdown_Implementation() …;

    protected override Task<bool> AuthorizeRequestAsync(int methodId)
        => Task.FromResult(methodId != MethodIds.Shutdown || _isAdminSession);
    // 거부: non-one-way → PermissionDenied 오류, one-way → 폐기. 메서드 존재 여부도 노출 안 함(조회 전 판정).
}
```

## 5. 오류 위생 — 내부 예외 문구 새는 것 막기

```csharp
hub.SendErrorDetails = false;   // 기본 true(개발 편의). 인터넷 노출 엔드포인트는 false 권장.
```

`false` 시 `Unhandled` 원격 응답은 고정 문구. 서버 측 `Trace` 기록은 설정과 무관하게 항상 남는다(운영자 관측 보장).

엔진 진단 전부(미처리 예외·디스패치 방화벽 기록 포함)는 `System.Diagnostics.Trace` 로 나간다 — 상용 호스트에서는 `TraceListener` 를 달아 파일·로그 파이프라인으로 연결해야 관측된다(기본 리스너만으로는 헤드리스 서비스에서 사라진다).

## 6. 큐 정책 — 슬로로리스·프레임 폭탄

```csharp
var queue = new MessageQueueOptions
{
    FrameTimeout = TimeSpan.FromSeconds(10),  // 기본 30초 — 첫 바이트 도착 후 프레임 완성 마감(완전 유휴 미적용)
    MaxFrameLength = 1 << 20,                 // 기본 4MB
};
using var client = await RpcClient.ConnectAsync(host, port, key,
    channel => new GameClientHub(h => HubSessionFactory.CreateRudpSession(channel, h, queue)));
```

## 7. 운영 신호 — 포화·백프레셔 관측

```csharp
await using var handle = await GameServerHub.ListenAsync(port, 100, key, factory, onConnected);
int live = handle.ActiveConnectionCount;        // 수락된 peer 수 — 상한 포화 근사 지표
// 수용 직후 끊긴 피어(구독 전 단절)도 즉시 회수되므로 이 값은 실제 생존 peer 수를 따라간다.

hub.Disconnected += () =>
{
    if (hub.LastDisconnectReason == DisconnectReason.FlowControl)
        metrics.BackpressureDisconnects.Increment();   // 수신 미처리 상한 단결 — "서버가 느리다"의 정확한 신호
};
```

## 8. 호출 취소 — 로비 이탈 등

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
int result = await client.MatchMakeAsync(request, cts.Token);
// 취소는 대기만 끊는다: 송신된 요청은 회수 안 됨(서버 구현은 완주), 늦은 응답은 무시, 세션은 재사용 가능.
```

## 9. 호출별 타임아웃 — 혼합 부하 예산 (v2.10.0)

허브 기본 `RpcTimeout`(30s) 하나로 섞인 부하를 다스리면 — 느린 배치 호출에 맞춰 상한을 올리는 순간
나머지 전부의 보호가 약해진다. 호출에만 예산을 건다:

```csharp
[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 42, TimeoutMs = 400)]   // 이 호출만 400ms
int FastProbe();

[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 43, TimeoutMs = 120_000)] // 느린 배치는 2분
async Task<Snapshot> BuildSnapshotAsync();
```

- 미지정(-1)이면 허브 기본 상속 — 기존 계약의 생성 코드는 바이트 단위 불변.
- 런타임 직접 호출은 `RequestRPC(…, TimeSpan? timeout, …)` 오버로드(null=허브 기본).
- `TimeoutMs = 0`·음수는 컴파일 거부(DRPCGEN010), OneWay+TimeoutMs 는 경고(DRPCGEN011 — 대기가 없어 무의미).
- 만료는 `TimeoutException`, 취소와 동일하게 세션을 오염시키지 않는다(뒤늦은 응답 무시·재사용 가능 — §8 참조).

## 관련

- [[../03-Reference/Public-API|Public-API]] — 노브 전체 표 · [[../03-Reference/Performance|Performance]] — 핫패스 기준선
- [[../01-Overview/Feature-Spec|Feature-Spec]] F2·F6·F7 — 동작 규격

---
project: DS_RPC
type: reference
status: stable
tags: [reference, api, packages, nuget]
updated: 2026-09-13
---

# Public-API — 재구축 3.2.0

사용자가 실제로 만지는 표면만 싣는다. 생성 산출물(`{Hub}.g.cs`)의 멤버는 §생성기가 만드는 것 참고.

## NuGet 의존 (고정 버전)

`Directory.Build.props` 의 두 프로퍼티가 단일 사실원이다.

| 프로퍼티 | 값 | 패키지 |
| --------- | ----- | -------- |
| `MessageProtocolPackageVersion` | `3.0.0` | `MessageProtocol`(런타임 + analyzers/dotnet/cs 생성기 포함, GenericMessage 포함) |
| `CommunicationPackageVersion` | `2.5.1` | `Communication.Shared`, `Communication.Network.RUDP.{Shared,Client,Server}` |

저장소 자체는 어떤 형제 프로젝트 경로도 참조하지 않는다(`Source/Sandbox/Test`의 csproj에서 `ProjectReference` 가
`../../DS_…` 로 가는 경우 없음 — 계약 확인 항목). DRPC 패키지 자체 버전은 릴리스 태그(`v*`)가 권위 — 현재 **3.2.0**(상용 하드닝·가산 API(IsDisconnected·protected ProcessRequestAsync), minor).

### MessageCategory 니블 배분표 (MessageProtocol 3.0.0 마이그레이션, 2026-09-11)

3.0.0 부터 종류·ID·카테고리는 `[Message(MessageKind, id, MessageCategory)]` 단일 속성으로 선언한다(구문법
`StandaloneMessage`/`GroupRootMessage`/`GroupElementMessage`/`NonIdMessage`/`MessageCategory` 제거).

| 계열 | Category | 대상(종류·ID) | 비고 |
| ---- | -------- | ------------- | ---- |
| DRPC 프로토콜 | `Category1` | `ProcedureCallRequestMessage`(Standalone·0)·`ProcedureCallResponseMessage`(Standalone·1)·`ProcedureCallErrorMessage`(Standalone·2) | 기존 명시 ID 불변. `DRPC.Shared.Message` 네임스페이스가 `Message` 를 가려 완전 한정(`MessageProtocol.Message`) 필요 |
| Sandbox 게임 | `Category2` | `ChatLine`(Parent·11)·`GiftBox<T>`(Standalone·60)·`Token`(Standalone·61) | 기존 명시 ID 불변 |
| NonId 메시지 | 기본(Category0) | `Player`·`PlayerJoined`·`ScoreLine`·`ScoreBoard` + 테스트 인라인 | 3.0.0 제약: NonId 는 id·category 인자 사용 불가(MSGPROT018) |

특이 사항: `ShoutChatLine` 은 구문법 `[GroupElementMessage(0)]`(수동 위치 0) 이었으나 3.0.0 은 수동 id 0 을
표현할 수 없다(id 0 = 생략 → FullName 해시) — `[Message(MessageKind.Child)]` 해시 id 로 이전. 요소 id 와이어 **값**이
0 → 해시로 바뀌었으나 형식은 불변이고 Sandbox 양측 동시 재빌드로 무영향.

## DRPC.Attribute

```csharp
namespace DRPC;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteProcedure : Attribute
{
    public RemoteProcedure(RpcDeliveryMode mode = RpcDeliveryMode.ReliableOrdered, int methodId = -1);
    public RpcDeliveryMode Mode { get; }
    public int MethodId { get; }
    public bool OneWay { get; set; }      // named arg: OneWay = true
    public int TimeoutMs { get; set; } = -1;  // named arg: TimeoutMs = 400 — 호출별 응답 대기 상한(v2.10.0, 문서 누락 보강)
    public bool Validation { get; set; }  // named arg: Validation = true — _Implementation 전 _Validate 게이트(F14)
}

public enum RpcDeliveryMode
{
    Unreliable, ReliableUnordered, Sequenced, ReliableOrdered, ReliableSequenced,
}

/// 제네릭 프로시저(F12) — 타입 파라미터 슬롯별 허용 타입 사전 선언. AllowMultiple.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class GenericProcedureAttribute : Attribute
{
    public GenericProcedureAttribute(params Type[] types);             // 슬롯 0
    public GenericProcedureAttribute(int slot, params Type[] types);   // 지정 슬롯
    public int Slot { get; }
    public Type[] Types { get; }
}
```

`[RemoteProcedure]` 인자 없이 붙이면 ReliableOrdered·MethodId 는 **이름 해시**(인터페이스 FQN+메서드명+매개변수 시그니처의 FNV-1a 32비트)로 자동 할당된다 —
선언 순서와 무관하게 이름이 같으면 항상 같은 값. 해시 충돌(한 계약 안 중복 MethodId)은 DRPCGEN005 컴파일 에러로 차단 → 충돌 메서드만 명시 methodId 지정.
**주의**: 첫 positional 인자는 `mode` 다. `[RemoteProcedure(0)]` 은 methodId 0 이 아니라 `Unreliable` —
methodId 만 지정할 때는 `[RemoteProcedure(methodId: 3)]` 을 쓴다(ADR-0002 결정 2).

제네릭 프로시저: `[RemoteProcedure(methodId: 7)] [GenericProcedure(typeof(int), typeof(string))] T GetDefault<T>();` —
타입 파라미터가 [GenericMessage] 파라미터(`void Unwrap<T>(GiftBox<T> box)`)로만 쓰이면 [GenericProcedure] 없이
그 메시지의 구성 선언에서 T 집합을 상속한다. 미선언 타입 인자 호출은 컴파일(DRPCGEN008)·런타임(스텁 throw) 양쪽에서 에러.
선언 결함은 DRPCGEN007(미선언 슬롯)·009(무효 선언·비-ID-헤더 [GenericMessage] 타입·제약·64구성 상한).

## 계약 인터페이스 (DRPC.Shared)

```csharp
namespace DRPC.Shared.Interface;

public interface IServerProcedureDeclarations { }   // 서버가 구현, 클라이언트가 호출
public interface IClientProcedureDeclarations { }   // 클라이언트가 구현, 서버가 호출
```

메서드는 **plain 반환 타입**으로 선언한다(`int Add(int a, int b);`) — 생성된 스텁이 이미 async.

## 허브 베이스

```csharp
namespace DRPC.Client.Network;
public abstract class ClientHub<TSPD, TCPD> : HubBase<TSPD, TCPD>
    where TSPD : IServerProcedureDeclarations where TCPD : IClientProcedureDeclarations
{
    protected ClientHub(Func<HubBase, ISession> sessionFactory);
}

namespace DRPC.Server.Network;
public abstract class ServerHub<TSPD, TCPD> : HubBase<TSPD, TCPD> { /* 위와 동일 형태 */ }
```

`HubBase`(공용 런타임, 사용자 코드는 보통 상속만 한다):

| 멤버 | 의미 |
| ------ | ------ |
| `TimeSpan RpcTimeout { get; set; }` | 기본 30초. `Timeout.InfiniteTimeSpan`·0 이하는 무제한. 만료는 `TimeoutException`(만료 보고 문구는 그 호출에 실제 적용된 예산을 말한다). ticks 를 volatile 저장해 32비트 플랫폼(IL2CPP) 티어 방지. 만료 스캔 타이머(1Hz)는 시간 제한 대기가 하나도 없으면 주차한다(무제한 대기만 남아도 — 다음 시간 제한 호출 시 재생성) |
| `int MaxConcurrentIncoming { get; set; }` | 기본 0(무제한). 초과 시 non-one-way 는 `Overloaded` 오류, one-way 은 drop. **연결 직후·유휴 시에만 설정** |
| `DisconnectReason? LastDisconnectReason { get; }` | 관측된 마지막 끊김 사유(끊김 전 null) — `Disconnected` 핸들러 안에서 읽는다. `FlowControl` = 수신 미처리 상한 단결(백프레셔 신호, 형제 제안 P4) |
| `bool IsDisconnected { get; }` | `Disconnected` 이벤트가 이미 발화했는지. 이벤트는 허브 수명당 1회라 **구독 이전에 끊긴 허브는 이벤트를 받을 수 없다** — 리스너(`RpcHost`)의 즉시 회수·늦은 구독자의 사전 검사용 관측 신호 |
| `bool SendErrorDetails { get; set; } = true` | `Unhandled` 오류의 원격 응답에 예외 상세 실을지(기본 true·기존 동작). false면 고정 문구 전송 — 인터넷 노출 엔드포인트 권장. 서버측 Trace 기록은 항상 유지 |
| `protected virtual Task<bool> AuthorizeRequestAsync(int methodId)` | 호출 권한 검증 훯(기본 전부 허용). 서버 허브 override 로 메서드별 권한 검사 — 거부 시 non-one-way 는 `PermissionDenied` 오류, one-way 는 drop. 등록표 조회 전에 판정(메서드 존재 노출 없음) |
| `protected Task ProcessRequestAsync(ProcedureCallRequestMessage message)` | 수신 요청 디스패치(예외 방화벽 포함). 테스트 이중 관측용 시임 — 사용자 코드 직접 호출 비대상(생성 스텁도 호출하지 않는다) |
| `int MaxPendingCalls { get; set; }` | 기본 0(무제한). 응답 대기 중 outgoing 호출 상한 — 도달 시 새 호출은 즉시 `InvalidOperationException`(fail-fast). 검사·등록 경쟁으로 순간적 초과 가능(근사 강제) |
| `event Action? Disconnected` | 끊김 1회(대기 호출은 이미 실패 처리된 뒤) |
| `void Disconnect()` | 대기 취소 + 세션 끊김 + 이벤트 |
| `void Dispose()` | `Disconnect()` + 타이머·세마포어 정리 |

## 생성기가 만드는 것 (허브마다)

```csharp
public partial class GameClientHub : ClientHub<IGameServerProcedures, IGameClientProcedures>
{
    // 접속 (클라이언트 측) — connectionKey 단일 지정 또는 RpcEndpointOptions 일괄 지정
    public static Task<GameClientHub> ConnectAsync(string host, int port, CancellationToken ct = default);
    public static Task<GameClientHub> ConnectAsync(string host, int port, string? connectionKey, CancellationToken ct = default);
    public static Task<GameClientHub> ConnectAsync(string host, int port, RpcEndpointOptions options, CancellationToken ct = default);   // 키·DTLS·타임아웃 일괄

    // 리스닝 (서버 측) — ListenAsync(port, onConnected, ct) / (port, connectionKey, onConnected, ct) / (port, options, onConnected, ct) / (port, ct)
    public static Task<RpcListenHandle> ListenAsync(int port, string? connectionKey, Func<GameServerHub, Task> onConnected, CancellationToken ct = default);
    public static Task<RpcListenHandle> ListenAsync(int port, RpcEndpointOptions options, Func<GameServerHub, Task> onConnected, CancellationToken ct = default);   // 옵션 일괄

    // Outgoing 스텁 (Async 전용 — sync 스텁은 없다). 왕복 호출은 맨 끝 선택 CancellationToken 을 받는다.
    public Task<int> AddAsync(int value1, int value2, CancellationToken cancellationToken = default);
    public Task NoteAsync(string text);                       // OneWay — 대기가 없어 취소 토큰 없음

    // 제네릭 스텁(F12) — 일반 호출과 동일한 방법(T 추론 가능, 반환 전용은 명시)
    public Task<T> GetConfigAsync<T>(CancellationToken cancellationToken = default);   // await hub.GetConfigAsync<int>()
    public Task<string> DescribeAsync<T>(T value, CancellationToken cancellationToken = default);  // await hub.DescribeAsync(42)

    // Incoming: 사용자가 이 partial 을 구현한다
    private partial Task<int> EchoSum_Implementation(List<float> values);
    private partial Task<T> GetConfig_Implementation<T>();    // 제네릭은 타입 파라미터까지 동일하게
}
```

`RpcListenHandle` : `IAsyncDisposable`/`IDisposable`, `Task? ListenTask` (중지·취소 시 반드시 완료), `int ActiveConnectionCount`(수락된 peer 허브 수 — 형제 제안 P4 운영 신호).

## 헬퍼 (보일러플레이트 대체, 직접 호출할 일은 거의 없음)

| 타입 | 멤버 |
| ------ | ------ |
| `DRPC.Client.Network.RpcClient` | `Task<THub> ConnectAsync<THub>(string host, int port, string? connectionKey, Func<IMessageChannel, THub> hubFactory, CancellationToken ct = default)`, 오버로드 `ConnectAsync<THub>(host, port, connectionKey, int connectTimeoutMs, hubFactory, ct = default)` — 침묵 호스트 연결 실패를 상한 이내로 확정(Communication 2.0.1 `ConnectTimeout` 채택, 0=기본 약 5초, 음수는 `ArgumentOutOfRangeException`), `ConnectWithOptionsAsync<THub>(host, port, RpcEndpointOptions, hubFactory, ct)` — 옵션 일괄 지정(키·타임아웃·상한·CRC32c·DTLS) |
| `DRPC.Server.Network.RpcHost` | `Task<RpcListenHandle> ListenAsync<THub>(int port, string? connectionKey, Func<IMessageChannel, THub> hubFactory, Func<THub, Task>? onConnected, CancellationToken ct = default)`, 오버로드 `ListenAsync<THub>(port, int maxConnections, connectionKey, hubFactory, onConnected, ct = default)` — 동시 수락 연결 상한(연결 고갈 공격 방어, 0=무제한·음수 거부), `ListenWithOptionsAsync<THub>(port, RpcEndpointOptions, hubFactory, onConnected, ct)` — 옵션 일괄 지정 |
| `DRPC.Shared.Network.RpcEndpointOptions` | `ConnectionKey`·`ConnectTimeoutMs`(0=기본)·`MaxConnections`(0=무제한)·`EnableCrc32c`(양단 일치 필수 — 와이어 비호환, 검출 전용)·`ServerCertificate`(DTLS 서버 인증서)·`TlsTargetHost`(클라 검증 — SAN/CN 일치)·`TlsCertificateValidation`(클라 검증 — 핀닝 콜백, `RudpTlsOptions.GetSha256Fingerprint` 권장; 검증 수단 없으면 기본 거부 — F13, Communication 2.5.0 위임·[[../05-Decisions/0003-dtls-delegation-and-flat-options | ADR-0003]]) + `ToTransportOptions()` |
| `DRPC.Shared.Network.HubSessionFactory` | `IMessageConverter Converter`, `ISession CreateRudpSession(IMessageChannel, IHubBase)`, 오버로드 `CreateRudpSession(IMessageChannel, IHubBase, MessageQueueOptions?)`(FrameTimeout·MaxFrameLength 등 세션 큐 정책 — 형제 제안 P3), `RudpTransportOptions CreateTransportOptions(string? connectionKey, int connectTimeoutMs = 0, int maxConnections = 0, bool enableCrc32c = false, RudpTlsOptions? tls = null)`(각 0/false/null=미설정·음수 거부) |
| `DRPC.Shared.Network.RpcDeliveryMap` | `RudpSendOptions ToSendOptions(this RpcDeliveryMode)` — DRPC↔RUDP 열거형 유일한 대응 지점 |

## 오류 모델

와이어 오류는 `DRPC.Shared.RpcFaultException`(`CallId`, `ErrorCode`, 메시지=원문)으로 관찰된다.
`DRPC.Shared.RpcValidationFailedException`(primary constructor, 메서드명)은 서버 내부 신호 — `_Validate` false 시 디스패치가 던지고 허브가 `ValidationFailed`(7) 응답으로 변환한다(예상 거부라 `Unhandled` 트레이스에 남지 않음).

`[RemoteProcedure(Validation = true)]` 옵트인 시 디스패치는 `{Name}_Implementation` 호출 전
`private partial Task<bool> {Name}_Validate(매개변수 원본과 동일)` 을 먼저 기다린다(제네릭은 `{Name}_Validate<T...>`
열림 partial, 디스패치는 구성별 닫힌 타입으로 호출). **true 여야만** 구현이 호출된다. false → 구현 미호출 +
`ValidationFailed`(7) 응답(클라 `RpcFaultException` 관찰). one-way 는 응답 채널이 없어 조용히 스킵.
`_Validate` 미구현 시 partial 선언이 소거되어 컴파일 에러(fail-closed — F14).

`DRPC.Shared.Message.RpcErrorCode`:

| 상수 | 값 | 발생 지점 |
| ------ | ---- | ---------- |
| `Unhandled` | 1 | 피어 구현 본문 예외 (와이어) |
| `UnknownMethod` | 2 | 등록되지 않은 MethodId (와이어) |
| `Overloaded` | 5 | `MaxConcurrentIncoming` 초과 (와이어) |
| `PermissionDenied` | 6 | `AuthorizeRequestAsync` 훅이 요청 거부 (와이어) |
| `ValidationFailed` | 7 | `_Validate` false 로 `_Implementation` 호출 거부 (와이어, F14) |
| `Timeout` | 3 | 호출 측 `TimeoutException` — 와이어 코드로는 전송되지 않음 |
| `Disconnected` | 4 | 호출 측 `InvalidOperationException` — 와이어 코드로는 전송되지 않음 |

수신 디스패치: `DRPC.Shared.DRPCMessageHandler : MessageHandler`(3개 메시지 타입 라우팅 + 세션 끊김을 허브로 연결).

## 진단

| ID | 심각도 | 내용 |
| ---- | -------- | ------ |
| DRPCGEN001 | error | 허브 클래스가 `partial` 이 아님 |
| DRPCGEN002 | error | `ClientHub<,>`/`ServerHub<,>` 를 상속하지 않음(또는 형식 인자가 계약 아님) |
| DRPCGEN003 | error | 지원 안 되는 타입·`ref/out`·제네릭 메서드·`Task` 반환·중복 메서드명(오버로드) |
| DRPCGEN005 | error | 한 계약 안에서 MethodId 중복(명시 중복·이름 해시 충돌 포함) |
| DRPCGEN006 | error | `OneWay = true` 인데 반환이 void 가 아님 |

## 빌드·테스트

```powershell
dotnet build DRPC.slnx -c Release        # 5 라이브러리 + Sandbox 3 + Test 3
dotnet test  DRPC.slnx -c Release        # 140개 통과 (49 생성기 / 52 단위 / 39 E2E RUDP 루프백)
```

`Debug` 로 CLI 빌드하면 Roslyn 언어 서버가 `DRPC.CodeGenerator.dll`(bin/Debug) 을 점유해 복사가 실패할 수 있다 —
CLI 는 `Release` 를 쓴다([[../06-Troubleshooting/Known-Issues|Known-Issues]]).

## 관련

- [[../02-Architecture/Overview|Architecture Overview]] · [[../04-Guides/Getting-Started|Getting-Started]] · [[../05-Decisions/0002-async-only-delivery-and-payload|ADR-0002]]

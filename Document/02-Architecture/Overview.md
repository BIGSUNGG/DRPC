---
project: DS_RPC
type: architecture
status: stable
tags: [architecture, layers, data-flow, payload]
updated: 2026-09-14
---

# Architecture Overview — 재구축 2.0.0

RPC 계층만 구현한다. 전송은 DS_Communication(RUDP), 직렬화는 DS_MessageProtocol — **둘 다 NuGet 안정판 참조**(현재 Communication 2.7.0 · MessageProtocol 3.1.0, 버전 권위는 [[../00-AI/CONTEXT|CONTEXT]]),
형제 저장소 소스는 참조하지 않는다(근거: [[../05-Decisions/0002-async-only-delivery-and-payload|ADR-0002]] 결정 4).

## 패키지 그래프

| 패키지 | TFM | 의존 | 역할 |
| -------- | ------- | ------ | ------ |
| `DRPC.Attribute` | netstandard2.1 | 없음 | `[RemoteProcedure]`, `[GenericProcedure]`, `RpcDeliveryMode` — 계약 표면 |
| `DRPC.Shared` | netstandard2.1 | Attribute, MessageProtocol, Communication.Shared, Communication.Network.RUDP.Shared | `HubBase` 런타임, 와이어 메시지, 오류 모델, 수신 라우팅, 전송 매핑 |
| `DRPC.Client` | netstandard2.1 | Shared, Communication.Network.RUDP.Client | `ClientHub<,>` 베이스, `RpcClient` 접속 |
| `DRPC.Server` | netstandard2.1 | Shared, Communication.Network.RUDP.Server | `ServerHub<,>` 베이스, `RpcHost` 리스닝 |
| `DRPC.CodeGenerator` | **netstandard2.0** | Microsoft.CodeAnalysis.CSharp | Roslyn incremental generator(analyzer). DRPC 런타임·MessageProtocol **미참조** |

`LiteNetLib` 은 Client/Server 어디에도 직접 참조하지 않는다 — 2.0.0 이 채널(`IMessageChannel`)을 추상으로 노출한다.

```
사용자 코드(계약 인터페이스 + partial 허브)
        │  [RemoteProcedure]
        ▼
DRPC.CodeGenerator ──빌드 시──▶ {Hub}.g.cs (Async 스텁 · 디스패치 · 접속/리스닝 · 페이로드 헬퍼)
        │
        ▼
DRPC.Shared  HubBase ── ISession.SendAsync(msg, RudpSendOptions) ──▶ Communication.Network.RUDP ──▶ UDP
        │                       ▲
        └── MessageSerializer ──┘ MessageProtocol(직렬화·생성기)
```

## 왕복 흐름

**Outgoing** `await hub.AddAsync(2, 3)`

1. 생성된 `__WriteParams_{계약}_{메서드}` 가 매개변수를 flat 버퍼로 인코딩 → `byte[]`.
2. `HubBase.RequestRPC(methodId, payload, mode)` — `CallId` 를 `Interlocked` 로 할당(0 제외), `PendingCall`(TCS + 만료 tick) 등록.
3. `ProcedureCallRequestMessage` 를 `ISession.SendAsync(msg, mode.ToSendOptions())` 로 전송. 만료 시점은 허브 공용 1초 타이머가 스캔.
4. 응답/오류/타임아웃/끊김 중 하나로 완료. 응답 바이트를 `__ReadReturn_…` 가 디코딩해 반환 타입으로 돌려준다.
5. `SendRPC`(OneWay) 는 같은 경로에서 CallId 를 **0 고정**으로 보내고 대기표를 만들지 않는다.

**Incoming** RUDP 수신 → `MessagePipeline` → `DRPCMessageHandler`(타입별 라우팅) → `HubBase.OnReceiveRPC*`

- 요청: `ProcessRequestAsync` — `MaxConcurrentIncoming` 세마포어(0=무제한) → 미등록 MethodId 이면 `UnknownMethod` 오류
  (one-way 는 침묵) → 등록 위임 `{Method}_Requested` → 생성된 `__ReadParams` → 사용자 `{Method}_Implementation` →
  `__WriteReturn` → `ProcedureCallResponseMessage`.
- **one-way 판정은 `CallId == 0`** (수신 측 등록표 아님 — ADR-0002 결정 3).
- 응답·오류의 전송 방식은 **요청 MethodId 에 등록된 방식**(`MethodDeliveryModes`)을 따른다.
- 끊김: `ISession.Disconnected` → `NotifyDisconnected` → 대기 TCS 전부 실패 + `Disconnected` 이벤트 1회.

## 페이로드 인코딩 (DRPC 고유)

메서드마다 매개변수를, 반환은 별도 버퍼를 쓴다. 모두 `MessageBufferWriter`(리틀엔디안)에 **선언 순서대로** 이어 붙이며,
프레임 구분은 정적 타입으로 알고 있으므로 별도 헤더를 두지 않는다.

| 타입 | 기록 |
| ------ | ------ |
| 프리미티브(`int`·`float`·`decimal`·`char` …) | 고정 폭 `WriteXxx` |
| `string`(null 허용) | `WriteString` = int32(UTF8 길이, null = -1) + 바이트 |
| `enum` | 기반 정수 타입으로 캐스팅 후 `WriteXxx` |
| `T?`(프리미티브·enum nullable) | `WriteBoolean(HasValue)` + 값(있을 때만) |
| `byte[]` | int32 길이 + 원시 바이트 |
| `T[]` / `List<T>` | int32 개수 + 요소 반복(요소도 이 표의 규칙) |
| `[NonIdMessage]` 등 타입 고정 메시지 | `MessageSerializer.Serialize<T>(v, ref buf)` (생성된 1바이트 헤더 포함) |
| Standalone/Group/Generic 메시지 | `MessageSerializer.SerializeToWriter(v, ref buf)` — 런타임 타입 기준 dispatch, **그룹 다형성 보존** |

읽기는 `MessageBufferReader` 로 같은 순서를 거꾸로 밟는다(`DeserializeFromReader` 는 헤더의 ID 로 라우팅).
미지원: `ref/out` 매개변수, 사전류, `Task`/`Task<T>` 반환(계약은 plain 반환 타입 — DRPCGEN003), 제약 있는 타입 파라미터(DRPCGEN009), **표시 속성 없는 `IMessageSerializable<T>` 구현 타입(엄격 규칙 — `[Message]`/`[GenericMessage]` 필수)**. 제네릭 메서드는 [GenericProcedure] 선언 슬롯으로 지원(F12) — 미선언 타입 인자는 DRPCGEN007/008/009·런타임 백스톱.
타입 검증은 **선언부 우선** — 계약을 선언한 어셈블리에서 `[RemoteProcedure]` 메서드 단위로 바로 돈다(허브 불필요, 메서드 위치에 진단). 허브 프로젝트 측 검증은 메타데이터로 들어온 선언의 안전망(허브 위치 진단). 검증 실패 시 생성기는 정의 선언만 담은 스켈레톤을 배출해 사용자 partial 구현이 고아(CS0759 벽)가 되지 않는다. 계약 프로젝트가 `DRPC.CodeGenerator` 를 애널라이저로 참조해야 선언부 검증이 돈다(Sandbox.Contracts 참조).

## 수명

- 클라이언트: `ConnectAsync` → `RudpConnector` → 채널 → `RudpSession`(채널 소유) + `DRPCMessageHandler` + 허브.
  접속 실패는 `InvalidOperationException("Failed to connect to server.")`. 허브 `Dispose()` = `Disconnect()` + 타이머/세마포어 정리.
- 서버: `RpcHost.ListenAsync` → `RudpListener.Start` → 수락마다 허브 1개(peer 추적). 핸들 `Dispose()` 시
  리스너 Stop + peer 허브 Dispose + `ListenTask` 완료(취소로도 완료 — 레거시 미관찰 이슈 해소).

## 관련

- [[../01-Overview/Feature-Spec|Feature-Spec]] · [[../03-Reference/Public-API|Public-API]] · [[../05-Decisions/0002-async-only-delivery-and-payload|ADR-0002]]
- [[../06-Troubleshooting/Known-Issues|Known-Issues]]

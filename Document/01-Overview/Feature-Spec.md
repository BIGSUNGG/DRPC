---
project: DS_RPC
type: overview
status: stable
tags: [scope, spec, feature]
updated: 2026-09-14
---

# Feature Spec — 재구축 구현 기능 명세

레거시(`Legacy/`)가 지원한 기능 스펙을 새 프로젝트에서 이어 구현한다.
이 문서는 **구현할 기능의 범위·동작·수용 기준**을 정의한다.
레거시 동작 근거는 아카이브 문서: [[../../Legacy/Document/03-Reference/Public-API|Public-API (Legacy)]], [[../../Legacy/Document/02-Architecture/Data-Flow|Data-Flow (Legacy)]], [[../../Legacy/Document/06-Troubleshooting/Known-Issues|Known-Issues (Legacy)]].

## 구현 상태 (2026-09-09)

F1–F9·F11 구현 완료 + **제네릭 프로시저(F12)·**패킷 암호화(F13)**·구현 전 검증 게이트(F14)** 구현 완료**(`dotnet test DRPC.slnx` 141개 통과) — 형제 NuGet **MessageProtocol 3.1.0**(2026-09-14 채택 — KI-43 게이트 완결·CodeGenerator 의존성 전파, 와이어·공개 API 무변화; 3.0.0 파괴 변경 마이그레이션 이력·카테고리 배분표는 [[../03-Reference/Public-API|Public-API]]), **Communication.Network.RUDP.*/Communication.Shared 2.7.0**(2026-09-14 채택 — TargetHost 이름 전용 매칭 옵트인 전환[`TlsAllowNameOnlyCertificateMatch`]·2.6.0 DTLS 최적화 포함. CRC32c 무결성·흐름제어·프레임 상한·ConnectTimeout·끊김 레치 재생 + **DTLS 1.2 패킷 암호화** 채택). F10(Template)만 범위 밖.
**`v2.1.0` 릴리스** — 태그 푸시 → run 34136624883 success, 5개 패키지 2.1.0 NuGet 업로드 확인.
형제 스택은 NuGet 안정판으로만 참조한다(형제 저장소 소스 참조 없음).

## 원칙

1. **레거시 패리티 우선**: 아래 기능은 레거시와 동일한 동작이 기준이다. 단 §재구축 결정에서 확정한 항목은 새 설계가 기준이다.
2. **재구축으로 자연 해소되는 항목**은 새 이름/설계로 바로 반영한다 (§ 재구축 결정).
3. **형제 프로젝트 경계 유지**: 전송 = DS_Communication(RUDP), 직렬화 = DS_MessageProtocol. 직접 구현하지 않는다.
4. 수용 기준 미달 기능은 완료로 보지 않는다.

---

## 기능 목록

| ID | 기능 | 패키지 | 우선 |
| ---- | ------ | -------- | ------ |
| F1 | Attribute RPC 계약 | DRPC.Attribute | P0 |
| F2 | Hub 런타임 (HubBase) | DRPC.Shared | P0 |
| F3 | 와이어 메시지·오류 모델 | DRPC.Shared | P0 |
| F4 | 메시지 핸들러 라우팅 | DRPC.Shared | P0 |
| F5 | 코드 생성기 (Hub 스텁) | DRPC.CodeGenerator | P0 |
| F6 | 클라이언트 Hub·연결 | DRPC.Client | P1 |
| F7 | 서버 Hub·리스너 수명 | DRPC.Server | P1 |
| F8 | NuGet 패키징·게시 | Source/* | P1 |
| F9 | Sandbox 데모 | Sandbox/* | P2 |
| F10 | Template | TemplateSource/* | P2 |
| F11 | 테스트 인프라 | Test/* | P0–P2 |
| F12 | 제네릭 프로시저 호출 | DRPC.Attribute·CodeGenerator | P0 |
| F13 | 패킷 암호화 (DTLS 1.2 옵트인) | DRPC.Shared | P0 |

---

## F1 — Attribute RPC 계약 (`DRPC.Attribute`)

인터페이스 메서드에 원격 호출 계약을 표시한다.

- `[RemoteProcedure(RpcDeliveryMode mode = ReliableOrdered, int methodId = -1)]`, `OneWay` named 프로퍼티.
- 암시 MethodId 는 **이름 해시**(인터페이스 FQN+메서드명+매개변수 시그니처의 FNV-1a) — 선언 순서 재배치에도 값 유지(결정 2026-09-11, DRPCGEN004 폐기).
- 계약 마커 인터페이스: `IServerProcedureDeclarations` / `IClientProcedureDeclarations` (Shared).
- 메서드별 전송 방식(DRPC 자체 `RpcDeliveryMode`)을 계약에서 고정 — 전송 스택 열거형은 계약면에 노출하지 않는다(ADR-0002 결정 2).

### 수용 기준

- Attribute·enum **두 타입만으로** 계약이 완결되고 `DRPC.Attribute` 의 패키지 의존은 0이다.
- `methodId` 미지정 시 생성기가 DRPCGEN004 경고로 보완한다 (F5).
- 함정: 첫 positional 인자는 `mode` 다. `[RemoteProcedure(0)]` 은 methodId 0 이 아니라 `Unreliable`.

### 제네릭 프로시저 (F12)

`[RemoteProcedure]` 제네릭 메서드의 타입 파라미터 슬롯별 허용 타입을 사전 선언한다.

- `[GenericProcedure(params Type[] types)]` → 슬롯 0. `[GenericProcedure(int slot, params Type[] types)]` → 지정 슬롯. `AllowMultiple` — 슬롯마다 1회.
- 지원 형태 4가지: ① 반환 제네릭 `T Proc<T>()`, ② 매개변수 제네릭 `void Proc<T>(T v)`, ③ 복합 `T1 Proc<T1,T2,T3>(T2 v1, T3 v2)`(슬롯별 목록 × 데카르트 곱, 상한 64구성), ④ `[GenericMessage]` 파라미터 `void Proc<T>(Package<T> v)` — 미선언 슬롯은 파라미터 메시지의 [GenericMessage] 구성 선언에서 T 집합을 상속(이때 T 는 ID 헤더 메시지여야 한다 — 구성 등록이 `(MessageId, ClassId)` 디스패치를 요구, NonId·프리미티브는 DRPCGEN009).
- 호출·구현은 일반 프로시저와 동일 패턴: 클라 `await hub.XxxAsync(42)`(타입 추론) / `await hub.XxxAsync<int>()`(반환 전용 명시), 서버 `Task<T> Xxx_Implementation<T>(...)` partial.
- 제약: 타입 파라미터 제약(`where`) 미지원(DRPCGEN009).
- 함정: 와이어 구성 인덱스는 선언 순서(슬롯 0이 가장 느리게 도는 오도미터)로 결정적 산출 — 목록 순서를 바꾸면 기존 피어와 호환 깨짐(컴파일 타임 감지 불가, 문서로만 관리).

## F2 — Hub 런타임 (`HubBase`, DRPC.Shared)

RPC 호출의 실질 런타임 전부.

- **Outgoing**: `RequestRPC`(응답 대기, TCS) / `SendRPC`(one-way, **CallId 고정 0**). `MaxPendingCalls`(0=무제한) 상한 도달 시 새 호출은 fail-fast `InvalidOperationException` — 응답 불능 피어에 대한 대기 테이블 무한 적체(메모리 고갈) 방어.
- **CallId**: `Interlocked` 단조 증가, **비재사용**. 0은 one-way 예약.
- **타임아웃·취소**: `RpcTimeout`(기본 30s, `<= Zero`/Infinite면 무제한), Hub 공용 타이머 스캔(per-call CTS 없음). **호출별 타임아웃 정책(v2.10.0)**: `[RemoteProcedure(TimeoutMs = N)]`(양수 ms) 이 그 호출에만 예산을 적용 — 허브 기본은 그대로 두고 느린 배치 호출에만 넉넉한 상한(DRPCGEN010: 0/음수 거부, DRPCGEN011: OneWay+TimeoutMs 경고). 런타임 직접 호출은 `RequestRPC(…, TimeSpan? timeout, …)` 오버로드(null=허브 기본, 해석 동일). 왕복 호출은 선택 `CancellationToken` 수용 — 취소 시 대기 즉시 취소 완료·슬롯 반납, 늦은 응답·오류는 무시, 송신된 요청은 회수 안 함(수신측 구현은 끝까지 실행됨).
- **Incoming**: 수신 → 비블로킹 처리 → 응답. `MaxConcurrentIncoming`(0=무제한) 세마포어; 초과 시 요청은 `Overloaded` 오류, one-way는 drop. 호출 권한은 `AuthorizeRequestAsync` 훅(기본 전부 허용) — 거부 시 non-one-way 는 `PermissionDenied`, one-way 는 drop, 등록표 조회 전 판정으로 메서드 존재 미노출.
- **수명**: `Disconnected` 이벤트, `Disconnect()`, `IDisposable`; 끊김 시 `CancelPendingCalls`로 pending TCS 예외 완료.

### 수용 기준

- 응답 지연·중복 CallId는 대기 Task 없으면 무시(잘못된 완료가 없어야 함).
- `MaxConcurrentIncoming`은 **연결 직후·유휴 시에만** 설정하도록 문서화(사용 중 변경은 미지원).
- `MaxPendingCalls` 도달 시 즉시 예외로 실패(대기하지 않음); 슬롯 해제(응답·오류·타임아웃·끊김) 후 재시도 가능. 검사·등록 경쟁으로 순간적 초과 허용(근사 강제).

## F3 — 와이어 메시지·오류 모델 (DRPC.Shared)

| MessageId | 타입 | 필드 |
| ----------- | ------ | ------ |
| 0 | `ProcedureCallRequestMessage` | `CallId`, `MethodId`, `ParameterData` |
| 1 | `ProcedureCallResponseMessage` | `CallId`, `ReturnData` |
| 2 | `ProcedureCallErrorMessage` | `CallId`, `ErrorCode`, `Message` |

- `[Message(MessageKind.Standalone)]` 등록, MessageProtocol 직렬화.
- `RpcErrorCode`: `Unhandled` / `UnknownMethod` / `Timeout` / `Disconnected` / `Overloaded` / `PermissionDenied`.
- `Unhandled` 응답의 상세 문구는 `SendErrorDetails`(기본 true)로 제어 — false면 고정 문구(정보유출 방어), 서버측 Trace 는 항상 기록.
- 원격 오류는 호출측에서 `RpcFaultException`으로 관찰.
- **응답·오류의 전송 방식은 요청 MethodId 에 등록된 방식(`MethodDeliveryModes`)을 따른다.**
- **one-way 신호는 `CallId == 0`** — 수신 측 등록표로 판정하지 않는다(ADR-0002 결정 3).

### 수용 기준

- Unknown MethodId(non-one-way) → `UnknownMethod`; Implementation 예외 → `Unhandled`; 상한 초과 → `Overloaded`.
- `Timeout`·`Disconnected` 는 와이어 코드로 전송되지 않고 호출 측 `TimeoutException`·`InvalidOperationException` 로만 관찰된다(코드는 정의만 유지).
- 페이로드 이중 직렬화 제거는 **이번 범위 밖** ([[../../Legacy/Document/05-Decisions/0001-defer-double-serialization|ADR-0001 (Legacy)]] 유지).

## F4 — 메시지 핸들러 라우팅 (`DRPCMessageHandler`)

- 세션 수신 메시지 → 요청/응답/에러별 라우팅.
- 세션 끊김 시 `NotifyDisconnected` → pending 실패 처리와 연결.
- 라이브러리에 `Console.WriteLine` 등 콘솔 의존 금지(이벤트/콜백만).

## F5 — 코드 생성기 (`DRPC.CodeGenerator`)

Roslyn incremental generator. `partial` Hub + Hub 베이스 상속을 탐지해 스텁 생성.

- **탐지**: 클라 `ClientHub`, 서버 `ServerHub` 상속 partial class (명명 정렬 — [[../05-Decisions/0001-hub-naming-and-version-2|ADR-0001]]).
- **Outgoing**: `{Method}Async` **만** 생성(sync `[Obsolete]` 스텁 없음 — ADR-0002 결정 1). OneWay → `SendRPC`, 아니면 `RequestRPC`. 왕복 스텁은 맨 끝 선택 `CancellationToken`(기본 `default`)을 받아 `RequestRPC` 에 전달 — 취소는 대기만 즉시 종료(슬롯 반납·늦은 응답 무시)하고 송신된 요청은 회수하지 않는다. OneWay 는 대기가 없어 토큰을 받지 않는다. 매개변수 없는 스텁도 앞 쉼표 없이 토큰을 받는다.
- **Incoming**: `async Task<byte[]>` 디스패치(`{Name}_Requested`) + 사용자 `partial Task` / `Task<T>` `{Name}_Implementation`. `Validation = true` 면 구현 호출 전 `{Name}_Validate`(partial `Task<bool>`) 게이트를 건다(F14).
- **연결**: 클라 `ConnectAsync(host, port, connectionKey?, ct)`, 서버 `ListenAsync(port, connectionKey?, onConnected, ct)` → `RpcListenHandle`.
- **페이로드**: 메서드별 래퍼 메시지 타입 없이, 매개변수·반환을 `MessageBufferWriter` 에 선언 순서로 이어 붙인다(ADR-0002 결정 4).
  메시지 타입 값만 MessageProtocol 에 위임한다 — `[Message(MessageKind.NonId)]` 은 타입 고정 `Serialize<T>`/`Deserialize<T>`,
  ID 헤더 메시지(Standalone/Group/Generic)는 `SerializeToWriter`/`DeserializeFromReader`(그룹 다형성 보존).
- **진단**: DRPCGEN001 partial / 002 Hub 베이스 / 003 지원 타입·오버로드·`Task` 반환 / 004 명시 methodId 권장(warning) / 005 중복 methodId(error) / 006 OneWay+non-void(error).
  DRPCGEN001 은 실제로 발화해야 하므로 생성기 파이프라인 전제조건에서 `partial` 을 빼고(베이스명에 `Hub` 포함만으로 후보 선별) 판정한다.
- **제네릭 스텁(F12)**: 페이로드 첫 4바이트 = 구성 인덱스(와이어 포맷 불변, HubBase 불변). Outgoing 스텁은 제네릭 메서드 그대로 방출해 내부에서 `typeof(T)` 체인으로 구성별 닫힌 헬퍼에 캐스팅해 넘긴다(선언 밖 조합은 마지막 `else` throw — 런타임 백스톱). Incoming 은 구성 인덱스 `switch` 후 닫힌 타입으로 `_Implementation<T...>` 호출.
- **제네릭 진단**: 007 타입 파라미터 미선언(error) / 008 호출 지점 미선언 타입 인자(error — 미해결 `{Method}Async` 호출을 구조적으로 탐지, 명시 인자·매개변수 추론 모두 검사, 추론 불가 슬롯은 런타임 백스톱에 맡김) / 009 [GenericProcedure] 선언 무효(비제네릭 메서드·슬롯 범위 밖·중복·타입 중복·제약·구성 상한·[GenericMessage] 슬롯에 비-ID-헤더 타입)(error).

### 수용 기준

- 생성 단계: 탐지→수집→연결 배선→Outgoing/Incoming 스텁→페이로드 헬퍼. (레거시 6단계 중 "래퍼 타입 직렬화 생성"단계가 페이로드 인라인으로 흡수됨)
- 서버 Hub 기준 Outgoing=ClientDecls·Incoming=ServerDecls(클라는 반대).
- `DRPC.CodeGenerator` 는 `Microsoft.CodeAnalysis.CSharp` 만 참조한다 — MessageProtocol 생성기·형제 저장소 경로를 참조하지 않는다(F8 잔여 이슈의 구조적 해소).

## F6 — 클라이언트 Hub·연결 (`DRPC.Client`)

- Hub 베이스 `ClientHub<TSPD, TCPD>`(`DRPC.Client.Network`) + `RpcClient.ConnectAsync` — 2.0.0 의 `RudpConnector` 가 `IMessageChannel` 을 노출한다.
- 세션 어댑터 클래스는 두지 않는다: 2.0.0 `RudpSession(channel, converter, handlerFactory)` 을 `HubSessionFactory.CreateRudpSession` 이 조립한다. `LiteNetLib` 직접 참조 없음.
- 실패 시 `InvalidOperationException("Failed to connect to server.")`.

## F7 — 서버 Hub·리스너 수명 (`DRPC.Server`)

- Hub 베이스 `ServerHub<TSPD, TCPD>`(`DRPC.Server.Network`) + `RpcHost.ListenAsync`(2.0.0 `RudpListener.Accepted`); peer마다 Hub 인스턴스 + `onConnected(hub)` 콜백(예외는 `Trace` 로 격리, 콘솔 의존 없음).
- `RpcHost.ListenAsync` `maxConnections` 오버로드(2.2 이후) — 동시 수락 연결 상한(연결 고갈 공격 방어). 초과 접속은 즉시 거부되고 수락은 계속된다(Communication 2.0.1 `MaxConnections`).
- `ListenAsync` → `Task<RpcListenHandle>`(`IAsyncDisposable`): 리스너 Stop/Dispose + CT cancel, 등록 peer 정리.
- `RpcListenHandle.ListenTask`는 **관찰 가능하게** 노출/완료 보장(레거시 미관찰 이슈 해소).

## F8 — NuGet 패키징·게시 (완료)

- `Source/` 5개 라이브러리, TFM `netstandard2.1`(CodeGenerator 는 netstandard2.0), `IsPackable=true` (CodeGenerator는 `DevelopmentDependency`).
- 버전은 루트 `Directory.Build.props`(`MessageProtocolPackageVersion`·`CommunicationPackageVersion` 포함).
- 태그 `v*` → GitHub Actions pack·publish — 이미 존재하던 `.github/workflows/nuget-publish.yml`(런 이름 "NuGet Publish")가 그 동작이다.

### 채택: MessageProtocol 3.0.0 (2026-09-11, DRPC 릴리스 아님)

- 파괴 변경 마이그레이션 — 구문법 4종 메시지 속성·`MessageCategoryAttribute` 제거에 따른 전면 이전. 기존 명시 ID 불변(DRPC 0/1/2, Sandbox 11/60/61), 카테고리 계열별 배분(DRPC=Category1, Sandbox 게임=Category2 — 배분표는 [[../03-Reference/Public-API|Public-API]]), 생성기 `AttributeReferences` 신문법 `[Message]`+`MessageKind` 해독 교체, 신규 인식 테스트 1건 추가(총 133). DRPC 패키지 버전은 무변동(2.13.0).

### 게시 상태 (2.10.0)

- `v2.10.0` 태그 푸시 → run 34166254578 success, 5개 패키지 NuGet push 승인(5/5)·flatcontainer 색인 확인.
- 내용: 호출별 타임아웃 정책(`[RemoteProcedure(TimeoutMs)]` + `RequestRPC` TimeSpan? 오버로드 + DRPCGEN010/011). minor.
- `Source/Directory.Build.props` `<Version>` 2.10.0(릴리스 커밋 `e091aaf`).

### 게시 이력: 2.9.2

- `v2.9.2` 태그 푸시 → run 34165482893 success, 5개 패키지 NuGet push 승인·flatcontainer 색인 확인.
- 내용: MessageProtocol 2.3.4 → 2.3.7 채택(KI-41 안내형 와이어 거부 등) + 신뢰 경계 회귀 테스트. patch.
- `Source/Directory.Build.props` `<Version>` 2.9.2(릴리스 커밋 `4f97d3d`).

### 게시 이력: 2.9.1

- `v2.9.1` 태그 푸시 → run success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용(patch — 공개 API 무변화): 송신 핫패스 중간 배열 제거 + Communication 2.4.0 채택.
- `Source/Directory.Build.props` `<Version>` 2.9.1(릴리스 커밋 `fd090aa`).

### 게시 상태 (2.9.0)

- `v2.9.0` 태그 푸시 → run success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용: 형제 제안 P4 운영 신호(`ActiveConnectionCount`·`LastDisconnectReason`) + MessageProtocol 2.3.4 채택.
- `Source/Directory.Build.props` `<Version>` 2.9.0(릴리스 커밋 `ca98c88`).

### 게시 상태 (2.8.0)

- `v2.8.0` 태그 푸시 → run success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용: `SendErrorDetails` 노브(Unhandled 정보유출 방어) + 서버측 항상 Trace.
- `Source/Directory.Build.props` `<Version>` 2.8.0(릴리스 커밋 `7355010`).

### 게시 상태 (2.7.0)

- `v2.7.0` 태그 푸시 → run success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용: 형제 제안 P3 채택 — `CreateRudpSession` 큐 옵션(`MessageQueueOptions`: FrameTimeout·MaxFrameLength 등) 통과.
- `Source/Directory.Build.props` `<Version>` 2.7.0(릴리스 커밋 `4758919`).

### 게시 상태 (2.6.0)

- `v2.6.0` 태그 푸시 → run success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용: `AuthorizeRequestAsync` 호출 권한 훅 + `RpcErrorCode.PermissionDenied`(6) + 형제 3차 채택(Communication 2.3.0·MessageProtocol 2.3.2).
- `Source/Directory.Build.props` `<Version>` 2.6.0(릴리스 커밋 `f8e613d`).

### 게시 상태 (2.5.0)

- `v2.5.0` 태그 푸시 → run 34152401923 success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용: 형제 2차 채택(Communication 2.2.1 — CRC32c 무결성, MessageProtocol 2.3.1) + `RpcEndpointOptions`·`ConnectWithOptionsAsync`/`ListenWithOptionsAsync`(옵션 일괄 지정, null-모호성 없는 별명 메서드).
- `Source/Directory.Build.props` `<Version>` 2.5.0(릴리스 커밋 `314890d`).

### 게시 상태 (2.4.0)

- `v2.4.0` 태그 푸시 → run 34151310315 success, 5개 패키지 flatcontainer 확인(전부 HTTP 206).
- 내용: 왕복 RPC 호출자 취소 — 스텁 전부(일반·제네릭) 맨 끝 선택 `CancellationToken`, `RequestRPC` 취소 시 즉시 취소 완료·슬롯 반납·늦은 응답 무시.
- `Source/Directory.Build.props` `<Version>` 2.4.0(릴리스 커밋 `74569f3`).

### 게시 상태 (2.3.0)

- `v2.3.0` 태그 푸시 → run 34150180755 success (Require API key → Pack → Push, 5개 nupkg 푸시).
- 내용: `HubBase.MaxPendingCalls`(outgoing 대기 상한, fail-fast) + `RpcHost.ListenAsync` `maxConnections` 오버로드(연결 고갈 방어) — 리소스 고갈 방어 3종 세트 완성(수신 동시성·송신 대기·수락 연결).
- `Source/Directory.Build.props` `<Version>` 2.3.0(릴리스 커밋 `fab7836`).

### 게시 상태 (2.2.0)

- `v2.2.0` 태그 푸시 → run 34148443218 success (Require API key → Pack → Push, 5개 nupkg 푸시 로그 확인).
- 업로드된 5개 패키지: `DRPC.Attribute`·`DRPC.Shared`·`DRPC.Client`·`DRPC.Server`·`DRPC.CodeGenerator` 모두 2.2.0,
  flatcontainer 인덱스 전부 2.2.0 등재 확인(nupkg blob 유입은 지연 관측 — [[../00-AI/PENDING|PENDING]]).
- 내용: 형제 스택 채택(Communication 2.0.1 — RUDP 흐름제어·프레임 상한·ConnectTimeout, MessageProtocol 2.3.0) + `RpcClient.ConnectAsync` 연결 타임아웃 오버로드.
- `Source/Directory.Build.props` `<Version>` 도 2.2.0 으로 갱신(릴리스 커밋 `046a9a0`) — 태그가 권위이나 기본값도 일치.

### 게시 상태 (2.1.0)

- `v2.1.0` 태그 푸시 → run 34136624883 success (Require API key → Pack → Push, 모든 스텝 ✓).
- 업로드된 5개 패키지: `DRPC.Attribute`·`DRPC.Shared`·`DRPC.Client`·`DRPC.Server`·`DRPC.CodeGenerator` 모두 2.1.0,
  flatcontainer 등록 + nupkg HTTP 200 확인(유입 지연 약 4분 — 정상).
- `Source/Directory.Build.props` `<Version>` 도 2.1.0 으로 갱신(릴리스 커밋 `1edd8f3`) — 태그가 권위이나 기본값도 일치.

### 게시 상태 (2.0.0)

- `v2.0.0` 태그 푸시 → run 33979153588 success (Require API key → Pack → Push, 모든 스텝 ✓).
- 업로드된 5개 패키지: `DRPC.Attribute`·`DRPC.Shared`·`DRPC.Client`·`DRPC.Server`·`DRPC.CodeGenerator` 모두 2.0.0,
  registration `upper=2.0.0` 이고 flatcontainer nupkg HTTP 200.
- 워크플로는 태그에서 버전을 뽑아 `-p:Version`·`-p:PackageVersion` 으로 주입하므로 `Source/Directory.Build.props` 의
  `<Version>` 은 로컬 기본값일 뿐이다(태그가 권위).
- 유입(ingestion) 지연: 푸시 직후 몇 분간 flatcontainer·검색 API 가 구버전만 보여도 정상 — registration 으로 판정한다.

### 수용 기준

- 레거시 잔여 이슈였던 **CodeGenerator↔MessageProtocol CI pack 실패**가 재발하지 않는 구조 — 생성기가 형제 저장소 경로를 전혀 참조하지 않으므로 저장소 단독 pack 이 성립한다.
- 태그 `v*` → GitHub Actions publish 워크플로는 **미구현**(범위 밖).

## F9 — Sandbox 데모 (`Sandbox/*`, 비배포)

- `Sandbox.Contracts`(계약·메시지) / `Sandbox.Server` / `Sandbox.Client`, TFM net10.0.
- Analyzer 규칙: `MessageProtocol` 패키지 참조는 `Sandbox.Contracts` 만 한다(NuGet 기본 PrivateAssets 가 analyzers 를 전파하지 않음 → Client/Server 로 안 퍼진다). Client/Server 는 `DRPC.CodeGenerator` 를 `OutputItemType=Analyzer` 로만 참조.
- 기본: `127.0.0.1:9050`, key `sandbox-key`. 데모 내용: 기본 ReliableOrdered 호출, `Sequenced`/`ReliableUnordered` 오버라이드, OneWay, DTO(NonId) 왕복, 양방향 역호출, 그룹 다형성(`ShoutChatLine`), 검증 게이트(`TransferGold` — F14: 통과 호출은 구현 실행, `amount <= 0`·동일 플레이어 이체는 `_Validate` false → 코드 7 거부 관찰), stdin 종료.

## F10 — Template (`TemplateSource/`, 비배포)

- 계약·Hub·생성기 사용 최소 템플릿(앱 스캐폴드 용도).

## F11 — 테스트 인프라 (`Test/*`)

| 단계 | 프로젝트 | 내용 | 결과 |
| ------ | ---------- | ------ | ------ |
| 단위 (P0) | `Test/DRPC.Shared.Tests` | CallId·타임아웃·동시성 상한·오류 3종·one-way(CallId 0)·전송 매핑·핸들러 라우팅 | 19 통과 |
| 생성기 (P1) | `Test/DRPC.CodeGenerator.Tests` | DRPCGEN001–006, 생성 형태(Async 전용·`[Obsolete]` 금지·페이로드 인코딩·NonId/Group 경로), 생성 결과 컴파일 | 22 통과 |
| 루프백 E2E (P2) | `Test/DRPC.E2E.Tests` | 실제 RUDP(127.0.0.1) 왕복: 기본/오버라이드 전송·OneWay·DTO·양방향·UnknownMethod·Unhandled·타임아웃·ListenTask/끊김 + 생성 산출물 형태(리플렉션: Async 전용·접속 팩토리 서명·후킹 배치) | 17 통과 |

---

## 재구축 결정 (레거시 잔여의 자연 해소)

재구축이므로 레거시가 호환 때문에 미룬 항목을 **지금 확정**한다. 이견 시 ADR(`Document/05-Decisions/`)로 기록.

| 레거시 잔여 | 재구축 결정 |
| ------------- | ------------- |
| `DRPC.Server.Netwrok` 오타 | `Network`로 철자 정정 (신규 네임스페이스) |
| Hub 명명 혼동 (클라=ServerHub 등) | **결정(2026-08-31)**: 소유 주체 기준 정렬 — 클라 측 `ClientHub`(DRPC.Client), 서버 측 `ServerHub`(DRPC.Server). 레거시 별칭 제거 |
| 전송 스택의 신뢰도 enum 을 계약에 재사용 | **결정(2026-09-05)**: `DRPC.RpcDeliveryMode` 자체 정의 + `RpcDeliveryMap` name-based 매핑 — 2.0.0 에서 개칭·이동된 외부 열거형에 계약을 묶지 않는다(구 이름은 ADR-0002 에 기록) |
| 메서드별 래퍼 메시지 타입(`{Method}_Paramter`) | **결정(2026-09-05)**: 래퍼 타입 폐기, flat 페이로드 인코딩(오타 교정 자체가 무효화됨) |
| sync Outgoing `[Obsolete]` 생성 | **결정(2026-09-05)**: Async 전용 — 오픈 이슈 1 해소 |
| one-way 를 수신 측 등록표로 판정 | **결정(2026-09-05)**: `CallId == 0` 만 신호, `OneWayMethodIds` 폐기 |
| 암시 MethodId 를 선언 순서로 할당 | **결정(2026-09-11)**: 이름 해시(FNV-1a, FQN+메서드+시그니처)로 변경 — 순서 재배치에도 와이어 id 안정, DRPCGEN004 폐기, 충돌은 DRPCGEN005 차단 |
| Template 계약 파일명 오타 | 보류(F10 범위 밖) |
| 버전 `1.1.0` | **결정(2026-08-31)**: `2.0.0` 출발 |

근거: [[../05-Decisions/0001-hub-naming-and-version-2|ADR-0001]] (명명·버전), [[../05-Decisions/0002-async-only-delivery-and-payload|ADR-0002]] (스텁·전송·one-way·페이로드)

## 범위 밖

- 저수준 소켓/전송 구현 (→ DS_Communication)
- 범용 직렬화 엔진 (→ DS_MessageProtocol)
- TemplateSource(F10)
- 페이로드 이중 직렬화·버퍼 풀링 최적화 (측정 후, 형제 합의 필요)
- Communication 수신 핸들러 async 화 (형제 프로젝트 영역)
- 페이로드 와이어 포맷의 버전 간 호환(단방향 인코딩만 보장)

## F13 — 패킷 암호화 (DTLS 1.2 옵트인)

- **위임 구조** — 암호화 구현은 전송 스택(Communication 2.5.0, BouncyCastle DTLS 1.2)에 위임하고 DRPC 는 옵션 표면만 노출한다([[../05-Decisions/0003-dtls-delegation-and-flat-options|ADR-0003]]). `RpcEndpointOptions` 에 TLS 필드를 하나라도 설정하면 `ToTransportOptions()` 가 `RudpTlsOptions` 를 조립해 `RudpTransportOptions.Tls` 로 전달.
- **역할 분리 속성** — 서버: `ServerCertificate`(`X509Certificate2`). 클라: `TlsTargetHost`(SAN/CN 일치) 또는 `TlsCertificateValidation`(핀닝 콜백 — `RudpTlsOptions.GetSha256Fingerprint` 지문 비교 권장). 검증 수단 없는 클라는 서버 인증서 **기본 거부**(fail-closed — Communication 계약 승계).
- **기본 평문** — 미설정(null)이면 기존 동작·와이어 호환 100% 유지(하위호환). **양단 모두 암호화 모드여야 한다**(와이어 비호환 — 평문 끝단은 RUDP 연결까진 성공하지만 어떤 RPC 도 완료되지 않는다, CRC32c 와 동일 표기 계약).
- **시점** — 연결 확립(키 수락) 후 DTLS 핸드셰이크를 완료한 뒤에만 채널을 전달한다(핸드셰이크 상한 기본 15초 — Communication 기본값 승계, DRPC 표면에는 미노출). 실패·상한 초과는 연결 실패로 확정.
- **미노출** — `HandshakeTimeout` 등 세부 노브는 Communication 기본값으로 충분하다고 판단해 DRPC 표면에서 제외(필요 시 옵션 추가는 순수 가산).
- Sandbox `--tls`(서버: 자가서명 인증서 생성·지문 출력, 클라: `--tls <지문>` 핀닝) — 수동 왕복 확인 경로.

### 수용 기준

- E2E(실제 RUDP 소켓): 핀닝 왕복·TargetHost 왕복·핀 불일치 거부·검증 수단 없음 거부(fail-closed)·평문 클라이언트 비호환 5건 통과.

## F14 — 구현 전 검증 게이트 (`Validation` 옵트인)

- **옵트인** — `[RemoteProcedure(Validation = true)]` 만 게이트가 켜진다(기본 false — 기존 생성 텍스트·와이어 불변). 제네릭 메서드도 같은 `[RemoteProcedure]` 속성으로 지정한다(`[GenericProcedure]` 는 타입 슬롯 선언 전용이라 속성을 두지 않는다).
- **계약** — 사용자는 `private partial Task<bool> {Name}_Validate(매개변수 원본과 동일)` 을 구현한다(제네릭은 `{Name}_Validate<T...>` 열림 partial 하나, 디스패치는 구성별 닫힌 타입으로 호출). 디스패치가 `_Implementation` 호출 전 `await` 하고 **true 를 반환해야만** 구현을 호출한다 — 인자 수준 사전 검증(권한·범위·상태 체크) 용도.
- **실패 경로** — false 시 `DRPC.Shared.RpcValidationFailedException`(서버 내부 신호) → 허브가 `RpcErrorCode.ValidationFailed`(7) 오류 응답으로 변환, 클라는 `RpcFaultException.ErrorCode == 7` 로 관찰. one-way 는 응답 채널이 없어 구현을 조용히 스킵. 예상된 거부이므로 `Unhandled` 트레이스에 남지 않는다.
- **Fail-closed** — `Validation = true` 인데 `_Validate` partial 를 구현하지 않으면 partial 선언이 컴파일에서 소택되어 호출 지점이 컴파일 에러로 실패한다(검증 누락이 조용히 통과되는 일 없음).

### 수용 기준

- 생성기 형태 테스트 4건: 게이트 방출·partial 선언 동반·미구현 컴파일 실패·미지정 시 무방출(일반+제네릭 구성별).
- E2E(실제 RUDP 루프백) 3건: 통과 시 구현 호출·false 시 구현 미호출 + 코드 7·제네릭 구성별 게이트. `dotnet test` 총 131개 통과.

## 오픈 이슈

없음 — 레거시로부터 넘겨받은 오픈 이슈 1(sync Outgoing 스텁 존폐)은 **Async 전용**으로 확정 해소했다(ADR-0002 결정 1).

## 관련

- [[./Home|Home]]
- [[../02-Architecture/Overview|Architecture Overview]] · [[../03-Reference/Public-API|Public-API]] · [[../04-Guides/Getting-Started|Getting-Started]]
- [[../06-Troubleshooting/Known-Issues|Known-Issues]]
- [[../../Legacy/Document/01-Overview/Scope|Scope (Legacy)]]
- [[../../Legacy/Document/02-Architecture/Overview|Architecture Overview (Legacy)]]
- [[../_meta/Changelog|Changelog]]

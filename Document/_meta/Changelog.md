---
project: DS_RPC
type: meta
status: stable
tags: [meta, changelog]
updated: 2026-09-13
---

# Changelog

문서 변경 기록(최신 위). 코드 변경은 커밋 메시지로 추적한다.

## 2026-09-13 (3차)

- **상용 하드닝 리뷰 수정 반영(공개 API·와이어 무변화)** — 3각 리뷰의 확정 수정 4건:
  1. 타이머 주차 조건 확장: 시간 제한 대기(deadline > 0)가 하나도 없으면 주차 — 무제한(롱폴) 호출만 남은 피어의
     1Hz 틱 영구 유지 해소. 판정은 게이트 안에서 재평가(스캔 루프의 낡은 값으로 판정하면 TryAdd 직후
     `EnsureTimeoutTimer` 와의 재생성 경쟁이 열린다 — 기존 `IsEmpty` 와 동일한 경쟁 안전성 유지).
  2. `RpcHost` 조기 단절 피어 스윕 E2E 회귀 테스트 신규(수용→구독 창구 단절 허브 → `onConnected` 관측 후
     `ActiveConnectionCount` 0 확인 — 수정 전엔 1 유지로 8초 타임아웃 실패).
  3. Dispose 경쟁 테스트 관측 강화: `ProcessRequestAsync` 를 protected 로 노출하고 `TestHub.DispatchForTest`
     로 디스패치 태스크를 await — 방화벽 부재(수정 전)면 `ObjectDisposedException` 이 테스트로 샌다.
     (internal 심 + `InternalsVisibleTo` 는 PolySharp 내부 폴리필이 테스트 어셈블리로 새어 CS0433 을 내 폐기 —
     protected 노출은 기존 TestHub 패턴과 동일.)
  4. 타이머 주차 테스트의 고정 2.5초 수면을 `WaitUntilAsync` 폴링으로 교체(약 2.4초 단축·플레이크 창 축소).
- 테스트 2건 추가(무제한 잔존 주차·조기 단절 스윕) — 총 140개 통과(Shared 52·E2E 39·CodeGenerator 49).
- 문서: `Public-API`(RpcTimeout 주차 주석)·`Production-Hardening`(§5 TraceListener 상용 연결 권고)·
  `CONTEXT`(테스트 수·다음 릴리스 ≥ v3.2.0 가산 원장 주석) 갱신.

## 2026-09-13 (2차)

- **상용 하드닝 — HubBase·RpcHost 런타임 결함 5건 수정(공개 API·와이어 무변화, 가산 1건)**
  1. fire-and-forget 수신 처리 방화벽: `ProcessRequestAsync` 를 코어+방화벽으로 분리 — 서버 중지·세션 소멸과 경쟁하는
     세마포 해지(ObjectDisposedException)·끊긴 세션으로의 Overloaded 오류 송신이 미관측 태스크 예외로 새던 경로 차단(Trace 로만 남긴다).
     `finally` 의 `gate.Release()` 도 해지 경쟁을 명시적 무시 처리.
  2. 만료 보고 문구 정확화: `TimeoutException` 이 허브 기본 `RpcTimeout` 이 아닌 그 호출에 실제 적용된 예산
     (`[RemoteProcedure(TimeoutMs)]` 오버라이드 포함)을 보고 — `PendingCall` 이 effective 예산을 보관.
  3. 타임아웃 타이머 유휴 주차: 대기표가 비면 스캔 타이머를 반납, 다음 시간 제한 호출 시 재생성(이중 검사로 경쟁 차단).
     역호출을 한 번이라도 한 피어마다 1Hz 틱이 서버 수명 내내 쌓이는 유휴 비용·Dispose 누락 시 타이머가 허브를 고정하는 누수 해소.
  4. 조기 단절 피어 즉시 회수: 수용→구독 창구에 끊긴 허브는 `Disconnected` 이벤트(수명당 1회)를 못 받아 `RpcHost.peers` 에
     Stop 까지 남던 유출 — 신규 `HubBase.IsDisconnected` 관측 후 구독 직후 회거. `ActiveConnectionCount` 부풀림 동시 해소.
  5. `RpcTimeout` ticks volatile 저장 — 32비트 플랫폼(Unity IL2CPP armv7) TimeSpan 티어 방지.
- 테스트 4건 추가(호출별 예산 보고·타이머 주차/재생성·IsDisconnected 래치·Dispose 경쟁 무결) — 총 138개 통과.
- `Public-API`(`IsDisconnected`·RpcTimeout 티어 주석)·`Production-Hardening`(§7 ActiveConnectionCount 정확성)·`CONTEXT`(테스트 수) 갱신.

## 2026-09-13

- **NuGet 패키지 설명 영문 재작성** — 5패키지(`DRPC.Attribute`·`CodeGenerator`·`Shared`·`Client`·`Server`) `<Description>` 을 한국어 한 줄에서 자기완결 영문으로 보강(패밀리 소개 + 기능 목록 + TFM/Unity 2021.2+ 호환 + 참조 방법). 각 패키지가 NuGet 에서 단독 노출되는 점을 고려해 전부 단독 독해 가능하게 작성. 팩 검증(`dotnet pack` nuspec 확인) 완료.
- `03-Reference/Packages.md` 신규: 5패키지 TFM·의존·역할 표 + Description 원천 규약(csproj 가 원천, 문서는 요약만).

## 2026-09-11 (25차)

- **v3.1.0 릴리스(minor, 암시 id 와이어 파괴)** — 암시 MethodId 이름 해시 할당. methodId 생략 시 선언 순서 폴백을 폐기하고 이름 해시(인터페이스 FQN+메서드명+매개변수 시그니처의 FNV-1a 32비트)로 자동 할당. 선언 순서 재배치에도 와이어 id 유지(구동기: 순서 변경으로 id 가 몰래 밀리는 호환 파괴 방지). 명시 methodId 는 여전히 우선, 해시 충돌은 DRPCGEN005 컴파일 에러로 차단. DRPCGEN004(descriptor·발생부·관련 테스트) 삭제.
  릴리스 게이트: Release 빌드 0 오류 · 테스트 134/134 · Sandbox 전 시나리오(exit 0) 확인.
  코드 변경: `DRPC.CodeGenerator`(MethodMetadata 해시 폴백·DeclarationsMetadata ordinal 제거·진단 삭제)·`DRPC.Attribute`(XML 주석).
  테스트: 기존 DRPCGEN004 테스트를 이름 해시 안정성(동일 식별자 동일 값·선언 순서 무관) 2건으로 교체 — 총 134개 통과.
  문서: Feature-Spec(F1·재구축 결정 표)·Public-API(암시 할당·진단 표)·Known-Issues(함정 주석 갱신) 갱신.
  주의: methodId 생략 계약은 재빌드 시 와이어 id 가 바뀐다 — 클라/서버 동시 재빌드 필요.

## 2026-09-11 (24차)

- **v3.0.0 릴리스(major)** — MP 3.0.0 채택 대응 파괴 변경 릴리스. 계약 코드가 구 메시지 속성(`NonIdMessage` 등 4종)에서 `[Message(MessageKind, id, category)]` 신문법으로 이전 필요(DRPC 생성기도 신문법만 해독). `Source/Directory.Build.props` Version 2.13.0 → 3.0.0, README·CONTEXT 릴리스 사슬·Public-API 버전표 동기화.

## 2026-09-11 (23차)

- **Communication 2.5.1 채택(패치)** — `Directory.Build.props` `CommunicationPackageVersion` 2.5.0 → 2.5.1. 형제 상용 하드닝(수용 루프 생존성·TLS/plain 스트림 생성 가드·소켓 오류 로그 스로틀) 반영 — **공개 API 무변화**라 코드 마이그레이션 없음. Release 빌드 0 오류·테스트 133/133·Sandbox 전 시나리오(exit 0) 확인. 버전 표기 동기화: CONTEXT·Overview·Feature-Spec·Public-API. DRPC 패키지 버전 무변동(2.13.0).

## 2026-09-11 (22차)

- **MessageProtocol 3.0.0 마이그레이션** — 형제 파괴 변경 릴리스(단일 `[Message(MessageKind, id, category)]` 속성화) 대응. `Directory.Build.props` 2.3.9 → 3.0.0. DRPC.Shared 프로토콜 3종 신문법 이전(명시 ID 0/1/2 불변, Category1 — `DRPC.Shared.Message` 네임스페이스가 `Message` 를 가려 완전 한정 필요), Sandbox.Contracts 이전(11/60/61 불변·Category2, NonId 4종은 kind 만, `ShoutChatLine` 은 수동 id 0 표현 불가로 FullName 해시로 이전 — 형식 불변·양측 동시 재빌드로 무영향). 생성기 `AttributeReferences.MessageStyleOf` 가 구문법 이름 매칭 대신 `[Message]`+`MessageKind` 해독(위치·명명 인자 양쪽)으로 교체, 신규 인식 테스트 1건 추가(총 133). 문서 동기화: 카테고리 배분표 Public-API 신설, CONTEXT·Overview·Feature-Spec·Getting-Started·ADR-0002 구문법 표기 갱신. Sandbox 전 시나리오 통과(client exit 0). DRPC 패키지 버전 무변동(2.13.0).

## 2026-09-09 (21차)

- **v2.13.0 릴리스(minor)** — 생성 허브 `RpcEndpointOptions` 오버로드(옵션 사용 시에도 간단 경로 유지). 태그 `v2.13.0` → Actions verify(build Release + test — run 34318814935, 132 통과) success → publish → 5개 패키지 게시 확인(flatcontainer 2.13.0 등록). README·CONTEXT 릴리스 사슬·`Source/Directory.Build.props` Version 동기화.

## 2026-09-09 (20차)

- **생성 허브 `RpcEndpointOptions` 오버로드 추가** — 옵션(키·DTLS·타임아웃)이 필요한 순간에도 간단 경로 유지가 최우선 원칙. `RpcHubEmitter` 가 `ConnectAsync(host, port, options, ct)`·`ListenAsync(port, options, onConnected, ct)` 를 emit 하고, 기존 위임 본문 `(port, null, …)` 의 `string?`↔options 모호성(CS0121)은 `(string?)null` 캐스트로 해소. Sandbox Client/Server 가 `RpcClient.ConnectWithOptionsAsync`·`RpcHost.ListenWithOptionsAsync` + `HubSessionFactory` 중첩 람다 대신 생성된 오버로드 한 줄로 복귀. 생성기 형태 테스트 1건 추가 — 테스트 132(생성기 47 / 단위 47 / E2E 38, 실측). Public-API 버전표 실측 동기화(MP 2.3.9·Comm 2.5.0·릴리스 2.12.0) + 오버로드 표기 추가.

## 2026-09-09 (19차)

- **v2.12.0 릴리스(minor)** — F14 구현 전 검증 게이트. 태그 → Actions verify+publish success(run 34314132311) → 5개 패키지 게시 확인(registration 2.12.0 등록 — flatcontainer 는 유입 지연 후 전파). README 버전 표기·CONTEXT 릴리스 사슬 동기화.

## 2026-09-09 (18차)

- **패킷 암호화(F13) 구현 + 형제 최신 채택** — Communication 2.5.0(RUDP DTLS 1.2·BouncyCastle)을 옵션 표면으로 노출: `RpcEndpointOptions` 에 `ServerCertificate`(서버)·`TlsTargetHost`/`TlsCertificateValidation`(클라 검증, 둘 다 없으면 기본 거부 fail-closed) 추가, `ToTransportOptions()` 가 `RudpTlsOptions` 를 조립해 전달(위임 구조·세부 노브 미노출 — [[../05-Decisions/0003-dtls-delegation-and-flat-options|ADR-0003]]). 미설정 시 평문 100% 하위호환. MessageProtocol 2.3.7 → **2.3.9**, Communication 2.4.0 → **2.5.0** 채택(형제 저장소 수정 없음 — 이미 게시됨). Sandbox `--tls`(서버 자가서명 인증서·지문 출력, 클라 `--tls <지문>` 핀닝) 추가. 테스트 119 → 124(E2E 5: 핀닝·TargetHost 왕복, 핀 불일치·검증 수단 없음 거부, 평문 비호환). Feature-Spec F13·Public-API·Production-Readiness-Review 전제 1 갱신(기밀성·서버 인증 해소, 클라 인증은 여전히 앱 계층)·Production-Hardening §3.5 신설·ADR-0003 신규.
- **v2.11.0 릴리스(minor)** — 태그 → Actions verify+publish success → 5개 패키지 게시 확인. README 버전 표기 동기화.

## 2026-09-09 (17차)

- **게시·개발 CI 게이트 신설** — `nuget-publish.yml` 이 pack(컴파일)만 하고 **테스트 없이** NuGet 에 푸시하던 구조였다(모든 릴리스가 로컬 머신 신뢰 전용). 태그 푸시 시 `verify` 잡(build Release + test)을 먼저 돌리고 `publish` 가 `needs: verify` 로 의존 — 배포 게이트가 개발 게이트보다 느슨하면 안 된다는 형제 원칙(Comm 후반 31·MP 2.3.8)과 정렬. `ci.yml`(push/PR → build+test)도 신설해 릴리스 시점 이전에 파손을 잡는다. 패키지 불변(버전 무관) — 릴리스 없음. 형제 확인: MP v2.3.9(테스트·퍼즈·문서 전용 — 채택 불필요), Comm 무변동.

## 2026-09-09 (16차)

- **호출별 타임아웃 정책 구현(기능 영역 — 스펙 명시 후보)** — 허브 전역 `RpcTimeout` 하나로 혼합 부하(빠른 조회 + 느린 배치)를 다스리면 어느 한쪽 보호를 희생해야 했다. `[RemoteProcedure(TimeoutMs = N)]`(양수 ms) 로 호출 단위 예산: 미지정(-1)이면 허브 기본 상속 — 기존 계약의 생성 텍스트는 바이트 단위 불변(오버로드 추가만으로 공개 표면 순수 가산, `RequestRPC(…, TimeSpan?, …)`). 검증 강제: 0/음수는 DRPCGEN010 컴파일 거부(조용한 무시 차단 — MP KI-8 계열 원칙), OneWay+TimeoutMs 는 DRPCGEN011 경고. 테스트 111 → 119(생성기 5: 인수 방출·미지정 불변·무효값 2·경고, 런타임 2: 단축/무제한 방향 오버라이드, E2E 1: 실제 RUDP 위 400ms 예산 만료 — 취소 e2e 와 동일 스타일). Feature-Spec 타임아웃 절·런북 §9 신설.
- **v2.10.0 릴리스(minor)** — 태그 → run 34166254578 success → 5개 패키지 게시 확인. README·버전 표기 동기화. 형제 확인: MP v2.3.8(CI/test/docs 전용 — 채택 불필요), Comm HEAD=v2.4.1 변동 없음.

## 2026-09-09 (15차 — 신규 루프 재개)

- **MessageProtocol 2.3.4 → 2.3.7 채택** — 하위 저장소 누적 수정 중 DRPC 와이어 신뢰 경계에 직접 닿는 것: ①KI-41(2.3.7) 불신 헤더가 다른 등록 타입으로 라우팅되던 디스패치 복원의 블라인드 캐스트를 안내형 `InvalidDataException`(원인·상대 타입 명시)으로 교정 + 플래그 비트 불법 프레임 거부 예외를 `InvalidCastException` → `InvalidDataException` 으로 재분류, ②2.3.5 생성기 힌트 이름의 `+` 보존(중첩 타입 AD0001 크래시 방지), ③2.3.6 오류형 ClassId 엔트리 스킵. DRPC 소스는 예외 형식에 의존하지 않아(HubBase 전달이 catch-all) 코드 변경 불필요 — 채택을 고정하는 신뢰 경계 회귀 테스트 신규(`HubSessionFactory.Converter` 가 불법 플래그 프레임을 `InvalidDataException` 으로 거부 — 이빨 확인: 2.3.4 로 되돌리면 실패). Communication 2.4.1 은 평가 후 미채택(유일 코드 수정이 TCP/TLS 리스너 — DRPC 는 RUDP 단일). 테스트 110 → 111.
- **v2.9.2 릴리스** — 태그 → Actions run success → 5개 패키지 NuGet 게시 확인. README 버전 표기 동기화.

## 2026-09-09 (14차 — 루프 종료)

- **영역 4(구조) 검토 수행 + 발견 위반 1건 수정** — `RpcClient` 의 커넥터 접속·허브 조립 본문이 타임아웃 오버로드·`ConnectWithOptionsAsync` 에 2벌 존재 → `ConnectCoreAsync` 단일 경로로 통합(동작 보존, 110/110 통과). 검토 결론: HubBase(~450줄 응집적 런타임 핵심)·RpcHost·생성기 이미 3파일 분할·DRPCMessageHandler 박형 — 그 외 SRP/SOLID 위반 없음. 릴리스 없음(내부 리팩터만 — 미출시 커밋로 feature/improvement 에 대기, 병합은 사용자 몫).
- **루프 종료(/loop stop)** — 14반복 · 13커밋 · 9릴리스(v2.2.0→v2.9.1) · 테스트 77→110 · 벤치마크 기준선 확보 · 런북 완결.

## 2026-09-09 (13차)

- **[[../04-Guides/Production-Hardening|Production-Hardening]] 런북 신규** — 스펙 목표(「실제 상용 서비스 투입 수준」)의 운영자 면 문서. 12개 노브·기능을 8절 런북으로 통합: 접속 예산(ConnectTimeout·RpcEndpointOptions)·고갈 방어 3종(연결/수신/대기 — 「상용 배포 시 명시적 상한 필수」 명시)·CRC32c 무결성(양단 일치·검출 전용 한계)·호출 권한(AuthorizeRequestAsync)·오류 위생(SendErrorDetails)·큐 정책(FrameTimeout·MaxFrameLength)·운영 신호(ActiveConnectionCount·FlowControl)·호출 취소. 모든 예시 v2.9.1 실제 API 기준. Getting-Started §관련·CONTEXT 에서 연결. 문서 전용 — 릴리스 없음.

## 2026-09-09 (12차)

- **호출 취소 E2E 검증 추가(스펙 영역 2/F11)** — 4차(v2.4.0)의 취소 기능이 단위 테스트로만 끝났던 간극 메움. 실 RUDP 위에서: 토큰 예산(300ms)이 서버 구현 지연(2초)보다 먼저 대기를 끊는다·취소 후 동일 연결로 후속 호출 정상 왕복(세션 무오염)·서버 구현은 끝까지 실행. 테스트 109→110건. Source 변경 없음 — 릴리스 없음.

## 2026-09-09 (11차)

- **벤치마크 인프라 신설 + 핫패스 기준선 확정(스펙 영역 3 근거 기반)** — `Test/DRPC.Benchmarks`(BenchmarkDotNet·ShortRun, 솔루션 포함·게이트는 빌드만) 4종: 왕복 ~222ns/384B·수신 디스패치 ~109ns/312B·one-way ~21ns/32B·변환기 직렬화 ~58ns/344B(루프백 메모리 세션 — 전송 스택 제외). 신규 [[../03-Reference/Performance|Performance]] 노트에 기준선·방법론·변경 이력 기록. 이후 영역 3 작업은 이 기준선 대비 유의미한 개선이 있을 때만(근거 없는 최적화 금지 원칙 문서화). Source 변경 없음 — 릴리스 없음.
- 구현 중 벤치 코드 설계 결함 2건 자체 수정(존재하지 않는 헬퍼 호출→루프백 세션 재설계, BenchHub/BenchSession Route 혼동) — 게이트가 CS1061·MSB4025(csproj 주석 `--`)로 포착.

## 2026-09-09 (10차)

- **송신 핫패스 중간 배열 제거(스펙 영역 3 성능)** — `MessageProtocolConverter.Serialize` 이 메시지마다 `ToArray()` 중간 배열(직렬화→복사→재복사 이중 복사)을 내던 것을 `writer.Write(buffer.WrittenSpan)` 단일 복사로 교체 — 송신 호출당 GC 할당 1건 제거. 단위 계약 테스트(왕복 바이트 정합) 추가로 복사 전략 무관 정합 고정. 테스트 108→109건.
- **형제 4차 채택 — Communication 2.4.0** — 늦은 구독자 끊김 전달 보장(래치·재생), 호스트 중지 시 생존 세션 `Local` 통지·사망 피어 송신 예외화. 채택 중 NU1102(유입 지연 약 5분) 재관측 — http-cache 클리어 해소(6차와 동일 패턴, 운영 지식으로 확립).
- [[../00-AI/CONTEXT|CONTEXT]]·[[../01-Overview/Feature-Spec|Feature-Spec]] 상태 동기화.
- **`v2.9.1` 릴리스(patch)** — 태그 푸시 → run success, 5개 패키지 flatcontainer HTTP 206 확인. 게시 상태·README 표기 갱신.

## 2026-09-09 (9차)

- **형제 제안 P4 채택 — 운영 신호 노출** — ① `RpcListenHandle.ActiveConnectionCount`(수락된 peer 허브 수 — 포화·연결 상한 근사 지표), ② `HubBase.LastDisconnectReason`(끊김 전 null, `Disconnected` 핸들러 안에서 판독 — 이벤트 시그니처 불변 유지). 사유 전달은 `NotifyDisconnected` 2인자 오버로드(인터페이스는 C# DIM 기본 구현으로 기존 구현 호환)·`DRPCMessageHandler` 가 세션 이벤트에서 `e.Reason` 전달. `FlowControl`(수신 미처리 상한 단결)로 백프레셔 식별 가능. 단위 1건(FlowControl 관측)·E2E 1건(카운트 0→1→0). 테스트 106→108건.
- **형제 채택 — MessageProtocol 2.3.4** — 직렬화기 캐시 volatile 화(ARM 메모리 모델 정합)·동시 중복 등록 경쟁 수정·진입점 계약 테스트. Communication 2.3.1 재확인(변화 없음 유지).
- [[../03-Reference/Public-API|Public-API]] HubBase 표·RpcListenHandle 행, [[../00-AI/PENDING|PENDING]] P4 채택완료 처리, [[../00-AI/CONTEXT|CONTEXT]] 갱신.
- **`v2.9.0` 릴리스** — 태그 푸시 → run success, 5개 패키지 flatcontainer HTTP 206 확인. 게시 상태·README 표기 갱신.

## 2026-09-09 (8차)

- **`HubBase.SendErrorDetails` — Unhandled 오류 정보유출 방어 + 서버측 항상 Trace** — 식별된 마지막 영역 1/2 잔여 항목 처리. 기존: 구현 예외의 `ex.Message`(경로·내부 상태 포함 가능)가 원격 피어로 그대로 전송되는 반면 **서버 운영자에겐 아무 기록도 남지 않던 역전**. 이제 ① 노브(기본 true·기존 동작 유지 — 하위호환, false면 고정 문구 전송·인터넷 노출 엔드포인트 권장) ② 예외는 설정과 무관하게 항상 `Trace.TraceError`(콘솔 의존 금지 규약 준수). 단위 2건(기본 상세 전송·억제 시 미누출). E2E 기본 경로 단언("intentional failure") 유지 통과로 기본 동작 불변 실증. 테스트 104→106건.
- [[../03-Reference/Public-API|Public-API]] HubBase 표, [[../01-Overview/Feature-Spec|Feature-Spec]] F3, [[../00-AI/CONTEXT|CONTEXT]] 카운트 동기화.
- **`v2.8.0` 릴리스** — 태그 푸시 → run success, 5개 패키지 flatcontainer HTTP 206 확인. 게시 상태·README 표기 갱신.

## 2026-09-09 (7차)

- **형제 제안 노트 소비 + P3 채택 — `CreateRudpSession` 큐 옵션 통과** — DS_Communication `Proposals-Upstream.md`(신규) 확인: P1(TCP TLS·보류 질문으로 PENDING 등록), P2(CRC32c·v2.5.0 채택완료), P3(타임아웃 정책 통일 — ConnectTimeout 은 v2.2.0 완료, **FrameTimeout·MaxFrameLength 등 `MessageQueueOptions` 를 `CreateRudpSession` 선택 매개변수로 노출**), P4(운영 신호·후보 등록). E2E — 64바이트 상한 세션에서 소형 프레임 왕복 정상·초과 페이로드 송신 격리(2초 이내 실패)로 배관 실증. 테스트 103→104건.
- **형제 태그 확인 — 버전 유지 결정**: Communication 2.3.1(테스트·문서·제안노트), MessageProtocol 2.3.3(생성기 리팩터·golden 검증) 은 기능 차이 없음 → 패키지 버전 유지(2.3.0/2.3.2). FrameTimeout 기본값(30초·첫 바이트 후 마감 — 완전 유휴 미적용) 확인으로 잠재 유휴 단절 결함 부재도 확인.
- [[../03-Reference/Public-API|Public-API]] 팩토리 표, [[../00-AI/CONTEXT|CONTEXT]] 카운트, [[../00-AI/PENDING|PENDING]] P1/P4 등록.
- **`v2.7.0` 릴리스** — 태그 푸시 → run success, 5개 패키지 flatcontainer HTTP 206 확인. 게시 상태·README 표기 갱신.

## 2026-09-08 (6차)

- **`HubBase.AuthorizeRequestAsync` RPC 호출 권한 검증 훅** — 스펙 개선 영역 1(보안 「RPC 호출 권한 검증」) 처리. `protected virtual Task<bool>`(기본 전부 허용 — 하위호환), 서버 허브 override 로 메서드별 권한 검사(예: 관리자 전용 프로시저). 거부 시 non-one-way 는 신규 오류 코드 `RpcErrorCode.PermissionDenied`(6) 반환, one-way 는 폐기. **등록표 조회 전 판정** — 거부된 MethodId 의 존재 여부도 노출하지 않음. 단위 4건(기본 허용·거부 시 오류+미실행·one-way 조용한 폐기·훅 인자 검증).
- **형제 3차 채택 — Communication 2.3.0·MessageProtocol 2.3.2** — Communication: 파이프라인 검증 통일·**기본 공개 접속 키로 리슨 시작 시 경고**(DRPC null-키 리스너에 즉시 적용), MessageProtocol: 역직렬화 진입 와이어 헤더-MessageId 검증·컨텍스트 사전 사전크기 성능개선. 채택 중 NuGet 유입 지연(NU1102) 관측 — http-cache 클리어로 해소(약 1분 지연). 테스트 99→103건 통과.
- [[../03-Reference/Public-API|Public-API]] 오류 코드 표·HubBase 훅, [[../01-Overview/Feature-Spec|Feature-Spec]] F2·F3·상태, [[../00-AI/CONTEXT|CONTEXT]] 동기화.

## 2026-09-08 (5차)

- **형제 스택 2차 채택 — Communication 2.2.1·MessageProtocol 2.3.1** — Communication 2.1.0(TCP TLS — DRPC 미사용) 건너뙄고 2.2.0 **RUDP CRC32c 패킷 무결성 레이어**(옵트인, 프로토콜 처리 전 위반 패킷 폐기 — IPv4 UDP 체크섬 비활성 우회 방어) + 2.2.1 프레이밍 버퍼 압축 개선, MessageProtocol 2.3.1(PooledBuffer 이중 반납 수정·생성기 파싱 강화) 채택.
- **`RpcEndpointOptions` + `ConnectWithOptionsAsync`/`ListenWithOptionsAsync`** — 전송 옵션 일괄 지정 묶음 타입(`ConnectionKey`·`ConnectTimeoutMs`·`MaxConnections`·`EnableCrc32c`, `ToTransportOptions()`). 기존 매개변수 오버로드에 `string?` 와 같은 위치 null-리터럴 모호성(CS0121·런타임 미스바인딩)을 만들지 않도록 **별명 메서드**로 추가 — 기존 호출 전부 무영향. CRC32c 는 양단 일치 필수(와이어 비호환)·검출 전용 문서화.
- E2E — 양단 CRC32c 왕복 정상 + 미스매치(끄고 접속) 연결 수립 불가 확인, 단위 4건(매핑·기본값·음수 거부). 테스트 94→99건 통과.
- [[../03-Reference/Public-API|Public-API]] 헬퍼 표·신규 타입, [[../01-Overview/Feature-Spec|Feature-Spec]]·[[../00-AI/CONTEXT|CONTEXT]] 상태 동기화.
- **`v2.5.0` 릴리스** — 태그 푸시 → run 34152401923 success, 5개 패키지 flatcontainer HTTP 206 확인. 게시 상태·README 표기 갱신.

## 2026-09-08 (4차)

- **왕복 RPC 호출자 취소 지원** — 스펙 개선 영역 2(정확성 「호출 타임아웃·취소 누수」) 처리. `HubBase.RequestRPC` 가 선택 `CancellationToken` 수용(사전 취소 → 미송신·`OperationCanceledException`, 대기 중 취소 → 즉시 취소 완료·슬롯 반납·늦은 응답 무시, 송신된 요청 회수 안 함). 생성 스텁(일반·제네릭) 왕복 호출 전부 맨 끝 선택 토큰 매개변수 획득(매개변수 없는 스텁 포함, 무득수 앞 쉼표 없음), OneWay 는 대기 부재로 제외. 기존 호출 전부 소스 호환(선택 매개변수). 단위 — 사전 취소 미송신·취소 슬롯 반납(MaxPendingCalls 상한 1에서 재수용)·늦은 응답 무시, 생성기 — 스텁 서명 ct 핀 3건·one-way 무토큰 핀. 테스트 91→94건 통과.
- [[../03-Reference/Public-API|Public-API]] 스텁 예시, [[../01-Overview/Feature-Spec|Feature-Spec]] F2(타임아웃→타임아웃·취소)·F5 갱신.
- **`v2.4.0` 릴리스** — 태그 푸시 → run 34151310315 success, 5개 패키지 flatcontainer HTTP 206 확인. 게시 상태·README 표기 갱신.

## 2026-09-08 (3차)

- **`RpcHost.ListenAsync` `maxConnections` 오버로드** — 스펙 개선 영역 1(보안·리소스 고갈 「대량 호출·연결 고갈」) 처리. 동시 수락 연결 수 상한을 DRPC 리슨 경로에 노출(Communication 2.0.1 `RudpTransportOptions.MaxConnections` 채택 완결 — ConnectTimeout 에 이은 두 번째). 상한 도달 시 초과 접속은 즉시 거부되고 수락은 계속(연결 고갈 공격 방어), 0(기본)=무제한·동작 불변, 음수 거부. `HubSessionFactory.CreateTransportOptions` 세 번째 선택 매개변수 추가. E2E — 상한 1에서 첫 클라 정상 동작·초과 접속 즉시 거부 + 음수 거부, 팩토리 단위 4건. 테스트 87→91건 통과.
- [[../03-Reference/Public-API|Public-API]] 헬퍼 표, [[../01-Overview/Feature-Spec|Feature-Spec]] F7 갱신.
- **`v2.3.0` 릴리스** — 태그 푸시 → run 34150180755 success. 누적 단위: `MaxPendingCalls`(2차) + `maxConnections` 오버로드(3차) — 리소스 고갈 방어 3종 세트. 게시 상태·README 표기 갱신.

## 2026-09-08 (2차)

- **`HubBase.MaxPendingCalls` — outgoing 대기 테이블 상한** — 스펙 개선 영역 1(보안·리소스 고갈 「대기 호출 적체」) 처리. 응답 불능 피어에 대해 호출자가 무한정 쌓는 대기 CallId(메모리 고갈 표면)를 상한으로 끊는다. 기본 0(무제한·동작 불변), 도달 시 새 호출은 대기 없이 즉시 `InvalidOperationException`(fail-fast), 슬롯 해제(응답·오류·타임아웃·끊김) 후 재시도 가능, 음수 거부. 검사·등록 경쟁의 순간적 초과는 근사 강제로 문서화. 단위 테스트 3건(상한 fail-fast·슬롯 해제 재시도·기본 무제한·음수 거부). 테스트 84→87건 통과.
- **PENDING 해소·정리** — v2.2.0 nupkg blob 5개 패키지 전부 확인(예전 404는 URL 형식 오류 — `{id}.{version}.nupkg` 로 206). LSP 캐시 아티팩트 진단 7건 `lens_diagnostic_mark` false-positive 처리, [[../00-AI/PENDING|PENDING]] 항목 정리.
- [[../03-Reference/Public-API|Public-API]] HubBase 멤버 표, [[../01-Overview/Feature-Spec|Feature-Spec]] F2 outgoing·수용 기준 갱신.

## 2026-09-08

- **형제 스택 채택 — Communication 2.0.1·MessageProtocol 2.3.0** — 하위 신기능 채택 절차(스펙 § 하위 프로젝트 채택) 최초 실행. `CommunicationPackageVersion` 2.0.0→2.0.1(RUDP 메시지 채널 흐름제어 실패폐쇄·MaxFrameLength 수신 적용·peer id 재사용 채널 오염 수정·폴링 오류 추적 스로틀·`ConnectTimeout` 신설), `MessageProtocolPackageVersion` 2.1.0→2.3.0(공유 참조 동일성·중복 (MessageId, ClassId) 컴파일 타임 거부·백레퍼런스 가이드 예외·등록 순서 검증).
- **`RpcClient.ConnectAsync` 연결 타임아웃 오버로드** — 신설 `connectTimeoutMs` 로 침묵 호스트(패킷 블랙홄) 연결 실패를 상한 이내로 확정. 기존 시그니처는 그대로(기본값 유지·하위호환), `HubSessionFactory.CreateTransportOptions` 도 `connectTimeoutMs` 선택 매개변수 추가(0=미설정, 음수 거부). E2E 회귀 — 침묵 포트 300ms 상한 실패(3초 미만 완료) + 팩토리 계약 단위 5건.
- **E2E 포트 할당 임시 포트 전환** — 고정 시드(9600+7n)가 Windows 예약 포트 범위·선행 실행 잔여 리스너와 충돌해 "RUDP 리스너 바인딩 실패" 플레이크(9607·9621·9705 관측) — OS 배정 임시 포트(UdpClient(0) 확보)로 교체. 테스트 77→84건 통과.
- [[../00-AI/CONTEXT|CONTEXT]]·[[../01-Overview/Feature-Spec|Feature-Spec]] 상태·버전 동기화, [[../03-Reference/Public-API|Public-API]] 헬퍼 표 갱신.
- **`v2.2.0` 릴리스** — 태그 푸시 → run 34148443218 success, 5개 패키지 flatcontainer 인덱스 2.2.0 등재 확인. 게시 상태·README 버전 표기 갱신, [[../00-AI/PENDING|PENDING]] 신규(LSP 캐시 아티팩트·nupkg blob 유입 지연 관측).

## 2026-09-07

- **`v2.1.0` 릴리스 · NuGet 게시**: `Source/Directory.Build.props` `<Version>` 2.1.0 → 태그 `v2.1.0` 푸시 →
  run 34136624883 success, `DRPC.{Attribute,Shared,Client,Server,CodeGenerator}` 2.1.0 flatcontainer 등록·nupkg HTTP 200 확인.
  README(기능 목록에 제네릭 프로시저·패키지 버전·테스트 수 77), `CONTEXT`(릴리스 상태), `Feature-Spec`(게시 상태 2.1.0 절),
  `Public-API`(버전 표기 2.1.0) 갱신.

- **F12 제네릭 프로시저 구현** — `[GenericProcedure(params Type[]) / (int slot, params Type[])]` 속성 신규(DRPC.Attribute, AllowMultiple). 4가지 형태 지원: 반환 전용 `T Proc<T>()`·매개변수 `void Proc<T>(T v)`·복합 `T1 Proc<T1,T2,T3>(T2,T3)`(데카르트 곱, 64구성 상한)·`[GenericMessage]` 파라미터 `void Proc<T>(Package<T>)`(미선언 슬롯은 메시지 구성 선언에서 상속). 호출은 일반 프로시저와 동일(`hub.XxxAsync(42)` 추론 / `hub.XxxAsync<int>()` 명시), 서버 구현은 `Task<T> Xxx_Implementation<T>(...)` partial. 와이어: 페이로드 첫 4바이트 구성 인덱스(슬롯 0이 가장 느린 오도미터) — 포맷·HubBase 불변. 진단: DRPCGEN007(슬롯 미선언)·008(호출 지점 미선언 타입 인자 — 미해결 `{Method}Async` 호출 구조적 탐지, 추론 불가 슬롯은 런타임 백스톱에 위임)·009(무효 선언·[GenericMessage] 슬롯의 비-ID-헤더 타입·타입 파라미터 제약·상한). 런타임 백스톱: 스텁 `else` throw + 수신측 알 수 없는 구성 인덱스 → `RpcErrorCode.Unhandled`.
- 형제 의존 승격: `MessageProtocol` 2.0.0 → **2.1.0**(GenericMessage 포함 안정판, 형제 저장소 태그 `v2.1.0` 게시 후 핀). Communication 2.0.0 유지.
- 구현 중 발견·승격된 제약: MessageProtocol 제네릭 구성은 T 멤버를 런타임 메시지 디스패치로 직렬화 → `[GenericMessage]` 슬롯 타입은 ID 헤더 메시지(Standalone/Group)여야 하고 NonId·프리미티브는 런타임 크래시 대신 DRPCGEN009 로 컴파일 타임 거부.
- 테스트 58→77(CodeGenerator 제네릭 18케이스 포함 36·E2E 루프백 5케이스 추가 22·Shared 19). Sandbox 데모에 4형태 호출 추가(GetConfig/Describe/Blend/Unwrap+GiftBox). `Feature-Spec` F12 절·F1·F5, `Public-API` 속성·스텁 서명·진단, `CONTEXT` 상태 갱신.

## 2026-09-05

- **F8 완료 · `v2.0.0` NuGet 게시**: 기존 `.github/workflows/nuget-publish.yml`(`v*` 트리거)에 태그를 붙여 푸시 →
  run 33979153588 success, `DRPC.{Attribute,Shared,Client,Server,CodeGenerator}` 2.0.0 이 등록·다운로드까지 확인.
  F8 을 "범위 밖/미구현"으로 적었던 문서(Feature-Spec·CONTEXT·Home·Known-Issues)를 실제에 맞게 정정하고,
  NuGet 유입 지연·`NU5128` 정상 경고·워크플로 공백 실패 가드를 Known-Issues 에 추가.
- 재구축 F1–F7·F9·F11 구현 완료에 맞춰 문서 신규: `02-Architecture/Overview.md`(패키지 그래프·호출 흐름·페이로드 인코딩),
  `03-Reference/Public-API.md`(표면·NuGet 2.0.0 고정·오류 코드·진단·빌드 명령), `04-Guides/Getting-Started.md`,
  `06-Troubleshooting/Known-Issues.md`.
- `05-Decisions/0002-async-only-delivery-and-payload.md` 신규: Async 전용 스텁, DRPC 자체 `RpcDeliveryMode` + 매핑 경계,
  one-way = `CallId 0`, 메서드별 래퍼 타입 폐기(flat 페이로드). 4결정 기록.
- `01-Overview/Feature-Spec.md` 갱신: 위 결정을 F1·F3·F5·F6·F7·F8·F9·F11 에 반영, 구현 상태 절 추가(테스트 58개),
  오픈 이슈 1 해소로 "없음", 스테일했던 전송 열거형 명세 제거, `MethodReliableTypes` → `MethodDeliveryModes` 개칭 반영.
- `00-AI/CONTEXT.md` 현 상태 갱신(초기 스켈레톤 → 구현 완료, `-c Release` 사용 이유, 빌드·테스트·샌드박스 명령),
  `01-Overview/Home.md` MoC 확장.
- `06-Troubleshooting/Known-Issues.md` 에 함정 두 개 추가: `EmitCompilerGeneratedFiles` 전역 플래그가 참조 프로젝트 obj 에
  생성 코드를 덤프해 `CS0102`·`CS0111` 중복 정의 오류를 내는 문제(경험 기록)와, 새로 만든 프로젝트의 restore 전 LSP 가짜 오류.
- `Test/DRPC.E2E.Tests/GeneratedShapeTests.cs` 신규: 생성 산출물 형태를 리플렉션으로 검증 (Async 전용·접속 팩토리 서명·
  구현 후킹 배치) — 파일 grep 대신 어셈블리를 보아 테스트 횟수 53 → 58.

## 2026-08-31

- `05-Decisions/0001-hub-naming-and-version-2.md` 신규: Hub 명명 소유 주체 기준 정렬(`ClientHub`/`ServerHub`) + `2.0.0` 출발 결정.
- `01-Overview/Feature-Spec.md` 갱신: 명명·버전 결정 반영, sync Outgoing은 오픈 이슈로 보류.
- `01-Overview/Feature-Spec.md` 신규: 레거시 패리티 기반 재구축 기능 명세(F1–F11, 재구축 결정, 오픈 이슈).
- `01-Overview/Home.md` 신규: 사람용 진입점(스텁).
- `00-AI/CONTEXT.md`, `00-AI/CONVENTIONS.md` 신규: 에이전트 진입점·작성 규약(레거시 승계).

## 2026-09-09

- `03-Reference/Production-Readiness-Review.md` 신규: v2.10.0 상용 투입 평가(조건부 가능 — 인증/상한/버전 고정/관측성 4대 전제).
- **F14 구현 — 구현 전 검증 게이트**: `[RemoteProcedure(Validation = true)]` 옵트인 시 디스패치가
  `{Name}_Implementation` 앞에서 `partial Task<bool> {Name}_Validate(매개변수 동일)` 를 기다려 true 여야만 구현 호출.
  false 는 신규 `DRPC.Shared.RpcValidationFailedException` → `RpcErrorCode.ValidationFailed`(7) 오류 응답
  (one-way 는 조용히 스킵). 미구현 시 컴파일 에러(fail-closed). 일반+제네릭(`RpcHubEmitter.Generic.cs` 구성별) 이미터 적용.
  코드 변경: `DRPC.Attribute`(속성)·`DRPC.Shared`(오류 코드 7·예외·`HubBase` catch 분기)·`DRPC.CodeGenerator`(메타데이터·이미터 게이트/선언).
- 테스트 7건 추가(생성기 4 + E2E 3) — 총 131개 통과. `Feature-Spec`(F14 신규·F5 Incoming 보강)·
  `Public-API`(Validation·TimeoutMs 누락 보강·오류 코드 7·_Validate 계약) 갱신.
- Sandbox 데모에 검증 게이트 예시 추가: `TransferGold(methodId 9, Validation = true)` — 계약·서버 허브 `_Validate`/`_Implementation`·
  클라이언트 통과/거부(`RpcFaultException` 코드 7 catch) 왕복 실측 확인. F9 데모 목록 갱신.

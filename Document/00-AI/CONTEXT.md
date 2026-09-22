---
project: DS_RPC
type: context
status: stable
tags: [ai, context]
updated: 2026-09-14
---

# CONTEXT — 에이전트 진입점

재구축 중인 DS_RPC 저장소. 작업 시작 시 이 문서를 먼저 읽는다.

## 현 상태 (2026-09-09)

- **재구축 F1–F9·F11·F12·F13·F14 구현 완료.** `Source/` 5개 패키지(Attribute·Shared·CodeGenerator·Client·Server), `Sandbox/` 3개, `Test/` 3계층(144개 통과) + 벤치마크 1(DRPC.Benchmarks, [[../03-Reference/Performance|Performance]] 기준선).
- 형제 스택은 **NuGet 안정판만** 참조한다(`MessageProtocol` **3.1.0**(2026-09-14 채택 — KI-43 충돌 판정 게이트 완결·`MessageProtocol` 단일 설치로 CodeGenerator nuspec 의존성 전파, 와이어·공개 API 무변화), `Communication.Network.RUDP.*`·`Communication.Shared` **2.7.0**(2026-09-14 채택 — RUDP TLS TargetHost 이름 전용 매칭 옵트인 전환[`RpcEndpointOptions.TlsAllowNameOnlyCertificateMatch` 신설, fail-closed·옵트인 시 만료 인증서 거부] + 2.6.0 DTLS 송신 풀링·세마포어 폐기·폴링 백오프·TCP null-host 검증 통일 포함) — CRC32c 무결성·흐름제어·프레임 상한·ConnectTimeout·끊김 래치 재생 + **DTLS 1.2 패킷 암호화(F13)** 포함) — 형제 저장소 프로젝트 참조·하드 경로 없음.
- 저장소 루트 솔루션은 `DRPC.slnx`.
- 빌드·테스트는 **`-c Release`** 를 쓴다. `Debug` 는 언어 서버가 생성기 DLL 을 점유해 복사가 실패할 수 있다([[../06-Troubleshooting/Known-Issues|Known-Issues]]).
- 구현 범위·수용 기준의 권위 문서는 [[../01-Overview/Feature-Spec|Feature-Spec]](F12 제네릭 프로시저·F13 패킷 암호화 포함). 설계 결정은 [[../05-Decisions/0001-hub-naming-and-version-2|ADR-0001]], [[../05-Decisions/0002-async-only-delivery-and-payload|ADR-0002]], [[../05-Decisions/0003-dtls-delegation-and-flat-options|ADR-0003]]. 상용 투입 런북은 [[../04-Guides/Production-Hardening|Production-Hardening]].
- 미구현: F10 TemplateSource. (릴리스: … → `v2.11.0`(패킷 암호화 F13 + Comm 2.5.0·MP 2.3.9 채택, minor) → `v2.12.0`(구현 전 검증 게이트 F14, minor) → `v2.13.0`(생성 허브 `RpcEndpointOptions` 오버로드 — 옵션 사용 시에도 간단 경로, minor) → `v3.0.0`(MP 3.0.0 채택 대응 **major** — 계약 코드가 `[Message(MessageKind, id, category)]` 신문법 필요, 2026-09-11) — 5개 패키지 NuGet 게시 확인. 이후 2026-09-11 **Comm 2.5.1 채택(패치, API 무변화)** → `v3.1.0`(암시 MethodId 이름 해시 — 선언 순서 폴백 폐기·재배치에도 와이어 id 안정·DRPCGEN004 폐기, minor, 2026-09-11) → `v3.2.0`(상용 하드닝 — 디스패치 방화벽·유휴 타이머 주차(무제한 잔여 포함)·조기 단절 피어 회수·유효 예산 보고·volatile RpcTimeout, 가산 API `HubBase.IsDisconnected`·protected `ProcessRequestAsync`, minor, 2026-09-13) — 5개 패키지 NuGet 게시 확인(워크플로 1m27s 성공) → `v3.3.0`(형제 채택 — Comm 2.6.0·2.7.0·MP 3.1.0, `TlsAllowNameOnlyCertificateMatch` 옵트인 플래그 신설·옵트인 미설정 거부 회귀 테스트, minor, 2026-09-14) → `v3.4.0`(선언부 타입 게이트 — DRPCGEN003 허브 없이 계약 어셈블리에서 발동·`[Message]` 표시 속성 필수 엄격 규칙·검증 실패 스켈레톤 배출, minor, 2026-09-14) → `v3.5.0`(유니티 메인라인 통합 — 생성기 Roslyn 참조 기본 4.14.0→4.3.0 전환으로 Unity 6 에서 nuget.org 패키지 직접 소비·`-unity` 이중 라인 폐지, minor, 2026-09-14) → `v3.5.1`(주석 영어화 — 활성 코드 52개 .cs 주석 영어화·공개 API XML doc 전수 61곳·한글 예외 메시지 영어화. 런타임 API·와이어 무변화, review-until-clean 2라운드 통과, patch, 2026-09-22))
- 레거시 코드·문서는 `Legacy/` 아카이브. 동작 근거가 필요하면 레거시를 참고하되 **구현 대상은 Feature-Spec** 이다.

```powershell
dotnet build DRPC.slnx -c Release
dotnet test  DRPC.slnx -c Release
dotnet run --no-build -c Release --project Sandbox/Sandbox.Server   # + Client 별도 창
```

## 규칙

1. `Source/`, `Test/`, `Sandbox/`, `TemplateSource/` 변경 시 같은 턴에 `Document/` 갱신.
2. 문서 작성 규약은 [[../00-AI/CONVENTIONS|CONVENTIONS]].
3. 레거시 문서 링크는 **상대 경로 + 별칭** 형식만 사용(단축 링크는 Legacy/Document와 파일명 충돌로 Ambiguous).

## 관련

- [[../00-AI/CONVENTIONS|CONVENTIONS]]
- [[../01-Overview/Home|Home]]

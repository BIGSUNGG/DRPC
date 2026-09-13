---
project: DS_RPC
type: context
status: stable
tags: [ai, context]
updated: 2026-09-13
---

# CONTEXT — 에이전트 진입점

재구축 중인 DS_RPC 저장소. 작업 시작 시 이 문서를 먼저 읽는다.

## 현 상태 (2026-09-09)

- **재구축 F1–F9·F11·F12·F13·F14 구현 완료.** `Source/` 5개 패키지(Attribute·Shared·CodeGenerator·Client·Server), `Sandbox/` 3개, `Test/` 3계층(140개 통과) + 벤치마크 1(DRPC.Benchmarks, [[../03-Reference/Performance|Performance]] 기준선).
- 형제 스택은 **NuGet 안정판만** 참조한다(`MessageProtocol` **3.0.0** — 2026-09-11 파괴 변경 마이그레이션: `[Message(MessageKind, id, category)]` 단일 속성·카테고리 배분표는 [[../03-Reference/Public-API|Public-API]], `Communication.Network.RUDP.*`·`Communication.Shared` **2.5.1**(2026-09-11 채택 — 수용 루프 생존성·스트림 생성 가드 하드닝 패치, 공개 API 무변화) — CRC32c 무결성·흐름제어·프레임 상한·ConnectTimeout·끊김 래치 재생 + **DTLS 1.2 패킷 암호화(F13)** 포함) — 형제 저장소 프로젝트 참조·하드 경로 없음.
- 저장소 루트 솔루션은 `DRPC.slnx`.
- 빌드·테스트는 **`-c Release`** 를 쓴다. `Debug` 는 언어 서버가 생성기 DLL 을 점유해 복사가 실패할 수 있다([[../06-Troubleshooting/Known-Issues|Known-Issues]]).
- 구현 범위·수용 기준의 권위 문서는 [[../01-Overview/Feature-Spec|Feature-Spec]](F12 제네릭 프로시저·F13 패킷 암호화 포함). 설계 결정은 [[../05-Decisions/0001-hub-naming-and-version-2|ADR-0001]], [[../05-Decisions/0002-async-only-delivery-and-payload|ADR-0002]], [[../05-Decisions/0003-dtls-delegation-and-flat-options|ADR-0003]]. 상용 투입 런북은 [[../04-Guides/Production-Hardening|Production-Hardening]].
- 미구현: F10 TemplateSource. (릴리스: … → `v2.11.0`(패킷 암호화 F13 + Comm 2.5.0·MP 2.3.9 채택, minor) → `v2.12.0`(구현 전 검증 게이트 F14, minor) → `v2.13.0`(생성 허브 `RpcEndpointOptions` 오버로드 — 옵션 사용 시에도 간단 경로, minor) → `v3.0.0`(MP 3.0.0 채택 대응 **major** — 계약 코드가 `[Message(MessageKind, id, category)]` 신문법 필요, 2026-09-11) — 5개 패키지 NuGet 게시 확인. 이후 2026-09-11 **Comm 2.5.1 채택(패치, API 무변화)** → `v3.1.0`(암시 MethodId 이름 해시 — 선언 순서 폴백 폐기·재배치에도 와이어 id 안정·DRPCGEN004 폐기, minor, 2026-09-11). **v3.1.0 이후 미출시 가산 API(`HubBase.IsDisconnected`, protected `ProcessRequestAsync` 노출 — 2026-09-13)가 쌓여 있어 다음 패키지 릴리스는 minor(≥ v3.2.0)여야 한다.**)
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

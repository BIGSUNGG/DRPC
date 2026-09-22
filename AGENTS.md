# DS_RPC (DRPC) — AI 협업 규칙

이 파일은 pi가 모든 세션에 자동 로드한다. 아래 규칙은 사용자의 명시적 요청으로 채택되었다 (배경: `Document/05-Decisions/0004-harness-switch-to-pi.md`).
문서 구성: `Document/` (Obsidian Vault, 사람+AI 공동 참조). 하네스 구성표: `Document/00-AI/HARNESS.md`.

## 핵심 원칙 (절대 규칙)

### 1. 문서 우선 — Document/를 항상 읽고 갱신한다

- **작업 시작 시**: `Document/00-AI/CONTEXT.md` → `Document/00-AI/GLOSSARY.md` → 작업 영역 문서(`01-Overview/`, `02-Architecture/`, `03-Reference/`) 순으로 읽고 맥락을 파악한다. 관련 문서를 읽지 않고 코드를 고치지 않는다.
- **변경 시**: 기능 추가·수정·제거, 구조·규약·결정 사항이 생기면 **같은 세션에서** `Document/`에 상세히 문서화한다. 절차는 `doc-sync` 스킬을 따른다 (아키텍처/레퍼런스 문서, `_meta/Changelog.md`, 필요시 ADR).
- 문서 갱신이 불가능하거나 불필요하면 그 이유를 사용자에게 명시적으로 보고한다. 조용히 넘기지 않는다.
- **README 갱신**: 기능이 추가되거나 수정되었을 때는 `README.md`에도 반영한다 — 라이브러리 사용자 관점의 기능 소개를 유지한다.
- doc-guard 훅이 세션 종료 시점에 코드-문서 불일치를 감시한다.

### 2. 능동 질문 — 추측으로 진행하지 않는다

- 요청에 정해야 할 것, 애매한 것, 궁금한 것이 있으면 **반드시 먼저 질문한다** (`ask_user_question` 등). 답을 기다린 뒤 진행.
- "아마 이것일 것이다"로 분기·API·동작을 결정하지 않는다. Vault에 기록되지 않은 사항은 추측하지 않는다 (`Document/00-AI/CONTEXT.md` 참조).
- 질문이 사용자 몰래 설계를 대신하는 것이 아닌, 선택지와 트레이드오프를 제시하는 방식이어야 한다.

### 3. OOP와 SOLID — 모든 코드 구현의 기준

- 모든 코드는 OOP와 SOLID를 지켜 구현한다. 상세 지침: `Document/00-AI/CONVENTIONS.md`.
- 동시에 과잉 추상화 금지 (YAGNI): 구현 1개짜리 인터페이스, 불필요한 팩토리/설정/스캐폴딩을 만들지 않는다.
- 규약 위반이 의심되면 리뷰어가 잡아낸다 (아래 리뷰 루프).

### 4. 요청 비판적 검토 — 문제 있으면 진행 전에 말한다

- 사용자의 요청을 구현 전에 비판적으로 검토한다. 기술적 문제, 규약 충돌, 유지보수 리스크, 더 나은 대안이 있으면 **문제가 될 부분을 구체적으로 제시하고** 계속 진행해도 되는지 **질문한 뒤** 진행한다.
- 비판 없이 그대로 진행하는 것도, 확인 없이 임의로 수정하는 것도 금지.

## 품질 게이트 — 리뷰어 Clean 판정 루프

- 코드나 개발 환경이 수정되었을 때(기능 추가/수정/제거, 리팩토링, 버그 수정, 빌드·설정 변경 등)에는 **구조·보안·속도** 측면을 검토하는 `reviewer` 서브에이전트로 검토받는다. 단, 서브에이전트 호출 전에 검토를 위해 호출하는 이유를 사용자에게 알리고 호출 여부를 질문한 뒤, 사용자가 허락한 경우에만 호출한다.
- 판정이 `ISSUES`면 수정 → 재검토를 **CLEAN이 나올 때까지 반복**한다. 절차: `review-until-clean` 스킬.
- 동일 이슈가 3라운드 이상 해결되지 않거나 이슈가 상호 모순이면 중단하고 사용자에게 보고·결정을 구한다.
- 오타 수정·포맷 변경 수준은 사용자 판단으로 생략 가능하다.

## 프로젝트 사실

- 정체성: DRPC — Unity/.NET용 RPC 프레임워크 라이브러리. 선언적 Hub·클라이언트 프록시·코드 생성기.
- 재구축 상태: 이 리포는 재구축 진행 중이다. 기존 RPC/RUDP 스택·샌드박스·테스트·문서는 `Legacy/` 아카이브(`Legacy/Source`, `Legacy/Test`, `Legacy/Sandbox`, `Legacy/TemplateSource`, `Legacy/Document`, `Legacy/DRPC.slnx`) — 명시적 요청이 아니면 `Legacy/`를 활성 진실 공급원으로 취급하지 않는다.
- 활성 소스: `Source/` 5개 패키지(Attribute·Shared·CodeGenerator·Client·Server), `Sandbox/`, `Test/`. 세부 현 상태: `Document/00-AI/CONTEXT.md`.
- 형제 스택: DS_RPC는 DS_MessageProtocol과 DS_Communication에 의존한다.
- 기술: C# (.NET/Unity 호환). 솔루션: `DRPC.slnx`. 코딩 규약: `Document/00-AI/CONVENTIONS.md`.
- 문서·보고·질문 언어: **한국어**.
- 변경 기록: 모든 의미 있는 변경을 `Document/_meta/Changelog.md`에 남긴다.

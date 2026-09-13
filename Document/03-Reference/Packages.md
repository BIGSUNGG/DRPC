---
project: DS_RPC
type: reference
status: stable
tags: [reference, packages, nuget]
updated: 2026-09-13
---

# Packages

NuGet 게시 5패키지. **패키지 설명(Description)의 원천은 각 csproj** — 이 문서는 요약만 담는다(전문 중복 시 부패).

| 패키지 | TFM | 의존 | 역할 |
| -------- | ----- | ------ | ------ |
| `DRPC.Attribute` | netstandard2.1 | (없음) | 계약 선언 — `[RemoteProcedure]`·`RpcDeliveryMode`·OneWay/TimeoutMs/Validation/제네릭 옵션. 계약 프로젝트 전용 |
| `DRPC.CodeGenerator` | netstandard2.0 | CodeAnalysis(팩 내 미포함) | Roslyn 소스 생성기 — Analyzer 참조. 스텁·디스패치·접속·페이로드 생성 + DRPCGEN 진단 |
| `DRPC.Shared` | netstandard2.1 | Attribute + MessageProtocol·Communication.Shared·Communication.Network.RUDP.Shared(NuGet) | 공통 런타임 — `HubBase`·와이어 메시지·`RpcFaultException` 오류 모델·수신 라우팅 |
| `DRPC.Client` | netstandard2.1 | Shared + RUDP.Client(NuGet) | 클라이언트 허브·RUDP 접속(DTLS 1.2 옵션) |
| `DRPC.Server` | netstandard2.1 | Shared + RUDP.Server(NuGet) | 서버 허브·RUDP 리스너(연결 키·인증 훅·DTLS 1.2 옵션) |

## NuGet Description 규약 (2026-09-13)

- 5패키지 Description을 **영어로 재작성** — 각 패키지가 NuGet에서 단독 노출되므로 전부 자기완결(패밀리 소개 1문장 + 기능 + TFM/Unity 호환).
- 원천: `Source/*/[PackageId].csproj`의 `<Description>`. 수정 시 이 문서가 아닌 csproj 를 고친다.
- 이형제: 형제 저장소 [[../../../../DS_Communication/Document/03-Reference/Packages.md|DS_Communication Packages]] — 단, 저장소 간 링크 불가이므로 경로만 기록.

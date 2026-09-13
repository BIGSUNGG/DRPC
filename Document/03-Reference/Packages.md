---
project: DS_RPC
type: reference
status: stable
tags: [reference, packages, nuget]
updated: 2026-09-14
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

## Unity 소비 — 메인라인 통합 (3.5.0부터)

Unity 6.0 LTS 번들 Roslyn = **Microsoft.CodeAnalysis 4.3.0.0**(에디터 `Data/DotNetSdkRoslyn` 실측). **v3.5.0부터 메인라인 생성기가 Roslyn 4.3을 직접 참조한다**(`Directory.Build.props` 기본값 4.3.0, 2026-09-14 사용자 결정) — CS9057 스킵 없이 Unity 에디터에서 바로 구동되므로, Unity 프로젝트는 nuget.org의 `DRPC.CodeGenerator`를 표준 버전으로 그대로 설치하면 된다. `.NET` 소비자 호환 범위는 Roslyn 4.3 호스트 이상(VS2022 17.3+/.NET SDK 6.0.4xx+, 2022 중반 이후)으로 유지된다(참조보다 낡은 호스트에서만 CS9057 발생).

- **로컬 피드** `C:/Projects/DS/unity-nuget/`는 오프라인·폴백 용도로 유지. 갱신은 게시된 것과 동일한 빌드로: `dotnet pack Source/DRPC.CodeGenerator -c Release -o artifacts/nupkg-unity -p:Version=<릴리스버전>` 후 복사. 구 `-unity` 접미사 라인(≤3.4.0)과 3.5.0 재빌드판은 이중 라인 시대의 유산 — nuget.org 3.5.0과 동일 비트이므로 놔둬도 무해.
- **검증**: nupkg 내 `analyzers/dotnet/cs/DRPC.CodeGenerator.dll`의 AssemblyRef가 `Microsoft.CodeAnalysis(.CSharp) 4.3.0.0`이고 TFM netstandard2.0(System.Reflection.Metadata 기반 확인). 스크립트: `artifacts/nupkg-unity/verify-nupkg.ps1`(gitignore).
- 새 Roslyn API 가 필요해 기본을 올릴 때는 Unity 호환 대가를 인지하고 결정한다(올리면 Unity 는 그 버전을 스킵한다 — CS9057).

## NuGet Description 규약 (2026-09-13)

- 5패키지 Description을 **영어로 재작성** — 각 패키지가 NuGet에서 단독 노출되므로 전부 자기완결(패밀리 소개 1문장 + 기능 + TFM/Unity 호환).
- 원천: `Source/*/[PackageId].csproj`의 `<Description>`. 수정 시 이 문서가 아닌 csproj 를 고친다.
- 이형제: 형제 저장소 문서 `DS_Communication/Document/03-Reference/Packages.md`(상대 경로 `../../../../DS_Communication/Document/03-Reference/Packages.md`) — 저장소 간 위키링크 불가이므로 경로만 기록.

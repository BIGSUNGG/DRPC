---
project: DS_RPC
type: reference
status: stable
tags: [reference, production, review, security]
updated: 2026-09-09
---

# Production-Readiness Review — v2.10.0 상용 투입 평가 (2026-09-09)

Unity 서버 상용 서비스 라이브러리 관점 전수 검토 기록. 코드 감사(HubBase·RpcHost·RpcClient·메시지·팩토리) +
형제 스택 검증(Communication 2.4.0 · MessageProtocol 2.3.7 로컬 소스) + `dotnet test -c Release` 119/119 통과 기준.

## 판정 — 조건부 투입 가능

런타임·생성기·테스트 품질은 상용 수준. 단, 아래 4가지 전제(운영 측 책임)를 충족해야 한다.

### 전제 조건 (미충족 시 투입 보류)

1. **네트워크 경계 설계** — 전송 계층에 인증·기밀성 없음. `ConnectionKey` 는 LiteNetLib `AcceptIfKey`
   **평문 비교**(스니핑·재생 가능 — 인증 아님), CRC32c 는 키 없는 체크섬(위변조 재계산 가능).
   인터넷 직결이면 앱 계층 세션 토큰 인증(첫 RPC 검증) + `AuthorizeRequestAsync` 메서드별 권한이 필수.
   VPC/내부망 전제라면 키 + 상한만으로 수용 가능.
   > **2026-09-09 갱신(v2.11.0)** — 기밀성·서버 인증 축은 전송 계층 옵션으로 해소: `RpcEndpointOptions` 의
   > DTLS 1.2 필드(F13, [[../05-Decisions/0003-dtls-delegation-and-flat-options|ADR-0003]] — 서버 인증서 + 클라 핀닝/TargetHost).
   > 단 클라이언트 인증·세션 토큰은 여전히 앱 계층 몫(DTLS 는 클라를 인증하지 않는다).
2. **상한 노브 명시 설정** — `MaxConnections`·`MaxConcurrentIncoming`·`MaxPendingCalls` 모두 기본 무제한.
   [[../04-Guides/Production-Hardening|Production-Hardening]] §2 참고. 기본값 그대로면 고갈 공격에 열려 있음.
3. **버전 고정 배포** — 페이로드 와이어 포맷 버전 간 호환 보장 없음(Known-Issues). 클라·서버 계약 어셈블리 동기
   배포 전략 필수(무중단 롤링 업데이트 불가 가정).
4. **관측성 연결** — 런타임 로그는 `System.Diagnostics.Trace` 뿐(구조화 로깅·메트릭 훅 없음).
   TraceListener/래핑 없이 배포하면 처리 예외가 어디에도 기록되지 않음.

### 강점 (감사 확인)

- 책임 분리: DRPC ~3.5k 라인만 자체 감사 대상(전송·직렬화는 NuGet 형제 스택).
- 비신뢰 페이로드 fail-closed: `MessageBufferReader` 가 음수 카운트·버퍼 초과·UTF-8·중첩 깊이 전부 예외 →
  처리 예외는 `Unhandled` 오류 응답/one-way 폐기로 수렴, 서버 크래시 경로 없음.
- DoS 3종 노브 + `FrameTimeout`/`MaxFrameLength`, 슬로로리스·프레임 폭탄 방어는 전송 계층에 존재.
- 오류 위생: `SendErrorDetails=false` 권장 문서화, 서버 Trace 는 설정과 무관 상시. 권한 거부가 lookup 전 판정
  (미등록 MethodId 존재 프라이버시 보존).
- E2E 루프백(실제 RUDP) 30 + 단위 47 + 생성기 42 = 119 통과. 지연·중복 응답 비오염, 취소/호출별 타임아웃 후
  세션 재사용 등 회귀 방지 테스트 상주(Known-Issues 표).
- 성능 기준선 존재([[../03-Reference/Performance|Performance]]) — 왕복 ~222ns/384B(루프백, 전송 제외).
- Unity 대응: netstandard2.1(Unity 2021.2+), 생성기 Roslyn 4.3 기준 참조(3.5.0+) — Unity 6 에디터에서 재빌드 없이 직접 구동.

### 잔여 리스크 (수용 가능 — 추적)

| 항목 | 등급 | 내용 |
| ------ | ------ | ------ |
| CallId 2^32 순환 | 낮음 | 누적 ~43억 호출 후 극히 오래된 지연 응답이 신규 호출과 충돌 이론 가능. 게임 서버 수명에서 사실상 무시. |
| 타임아웃 메시지 오표기 | 낮음 | `ScanTimeouts` 예외 문구가 호출별 타임아웃과 무관하게 `RpcTimeout` 표기(코스메틱). |
| `MaxConcurrentIncoming` 실행 중 변경 | 낮음 | 문서화된 제약. 위반 시 폐기된 세마포어 `Release` 로 unobserved 예외 1건 가능. |
| `RpcHost.Accepted` 내 hubFactory 예외 | 낮음 | 미보호 — 팩토리가 순수 조립이라 실질 위험 낮으나, throw 시 수락 경로 동작은 전송 계층 의존. |
| one-way 무음 실패 | 수용 | 대기표 없음 — 문서화된 설계. |
| 문서 경고 2건 | 미미 | CS0419(HubBase cref 모호), CS1574(RpcEndpointOptions cref). |

## 관련

- [[../04-Guides/Production-Hardening|Production-Hardening]] — 투입 런북(상한·키·CRC·암호화·권한·오류 위생)
- [[../06-Troubleshooting/Known-Issues|Known-Issues]] — 와이어 비호환·one-way 무음 등
- [[../03-Reference/Performance|Performance]] — 핫패스 기준선

# Portfolio — 윤준식 · 백엔드 개발자

**입고부터 정산까지, 물류 도메인을 시스템으로 풀어내는 백엔드 개발자입니다.**
C#, ASP.NET Core, MS SQL Server 환경에서 파편화된 서비스의 플랫폼 통합과 외부 ERP·커머스 등 이기종 시스템 연동을 아키텍처 설계부터 운영까지 주도해 왔습니다.

이 저장소는 기술경력서의 프로젝트를 **경력서 순서 그대로**, 담당업무 항목 하나하나가 **어떤 코드로 구현되었는지** 보여주기 위해 정리한 것입니다.

> **읽는 법** — 각 프로젝트 폴더의 `README.md` 가 "담당업무 문장 → 파일" 매핑표입니다.
> 하위 폴더 번호(`1.`, `2.`, …)는 경력서의 담당업무 번호와 같습니다. 파일 상단 주석에 `[담당업무 N]` 으로 표시되어 있습니다.
>
> **범위** — 재직 중인 회사의 소스를 그대로 올리지 않았습니다. 제가 설계한 구조·패턴·핵심 로직을 회사 데이터·브랜드·내부 규격을 제거하고
> 일반화해 다시 작성한 코드이며, **빌드·실행이 아니라 설계와 구현 방식을 읽기 위한** 것입니다.

---

## 프로젝트 (로젠풀필먼트 IT솔루션팀, 2025.02 ~ 재직중)

| # | 프로젝트 | 기간 | 규모 | 기술 스택 |
|---|---|---|---|---|
| 1 | [**통합 스케줄러 플랫폼**](src/01.UnifiedScheduler/) | 2026.04 ~ 2026.07 | 1명 (설계·개발·운영 단독) | .NET 8, ASP.NET Core, Hangfire, Cronos, Dapper, EF Core, MS SQL Server, Serilog + Seq, React, JWT, Polly |
| 2 | [**WMS 오케스트레이션 플랫폼 (MSA 전환)**](src/02.WmsOrchestration/) | 2025.05 ~ 2026.01 | 3명 (오케스트레이션 레이어·MQ 전체 설계·구현 담당) | .NET 8, ASP.NET Core Web API, MassTransit, RabbitMQ, Redis, EF Core, Dapper, PostgreSQL, SQL Server, Hangfire, YARP, Polly, SignalR, JWT |

### 1. 통합 스케줄러 플랫폼 → [`src/01.UnifiedScheduler/`](src/01.UnifiedScheduler/)
서비스별로 흩어져 운영되던 레거시 스케줄러를 단일 백엔드와 단일 관리 콘솔로 통합. 스케줄 설정을 코드에서 DB로 분리해, 설정 변경만으로 서비스가 확장되는 플랫폼으로 재설계.
**운영 전환 3개월 만에 75개 도메인 / 34개 브랜드로 확장, 누적 70만 건 시스템 예외 0건(실패는 전량 업무 예외) 무중단 운영.**
설계 결정과 이유는 [설계 문서](src/01.UnifiedScheduler/ARCHITECTURE.md) 참고.

| 담당업무 | 폴더 |
|---|---|
| 1) 설정으로 확장하는 구조 — 2계층 상속 설정 모델, 280건 브랜드 설정 무배포 운영 | [`1.ConfigurationInheritance/`](src/01.UnifiedScheduler/1.ConfigurationInheritance/) |
| 2) 표준화된 실행 파이프라인과 공통 프레임워크 — 8계층 파이프라인, 공통 Job 베이스, 파생 Job 69종 | [`2.ExecutionPipeline/`](src/01.UnifiedScheduler/2.ExecutionPipeline/) |
| 3) 장애 격리와 운영 자율화 — 큐·워커·알림 서버 분리, 3단 운영 제어 | [`3.FaultIsolationAndOperations/`](src/01.UnifiedScheduler/3.FaultIsolationAndOperations/) |
| 4) 운영 도구화와 안정화 — React 관리 콘솔, 조회 데드락·레거시 포맷 인시던트 | [`4.OperationsConsoleAndStabilization/`](src/01.UnifiedScheduler/4.OperationsConsoleAndStabilization/) |

### 2. WMS 오케스트레이션 플랫폼 (MSA 전환) → [`src/02.WmsOrchestration/`](src/02.WmsOrchestration/)
모놀리식 API를 MSA로 전환. WMS와 다수의 외부 쇼핑몰·물류 시스템 간 대량 데이터 연동을 위한 메시지 기반 오케스트레이션 플랫폼 구축.
**타임아웃·네트워크 장애·DB 락이 겹치는 상황에서도 서버 응답 장애 0건.**

| 담당업무 | 폴더 |
|---|---|
| 1) 분산 오케스트레이션 설계 — Job·Chunk·Step 3계층, Saga State Machine | [`1.DistributedOrchestration/`](src/02.WmsOrchestration/1.DistributedOrchestration/) |
| 2) 확장을 위한 Base Framework — 공통 상위 클래스, Attribute 바인딩 엔진 | [`2.BaseFramework/`](src/02.WmsOrchestration/2.BaseFramework/) |
| 3) 분산 환경의 장애 내성 — 이중 멱등성, Polly, Redis 분산 락, Outbox/Inbox, DLQ | [`3.FaultTolerance/`](src/02.WmsOrchestration/3.FaultTolerance/) |
| 4) 게이트웨이 및 인증 인프라 — YARP, 요청량 제한, SignalR, 이원화 DB, JWT·다중 테넌트 | [`4.GatewayAndAuth/`](src/02.WmsOrchestration/4.GatewayAndAuth/) |

## 개인 프로젝트

| # | 프로젝트 | 기간 | 규모 | 기술 스택 |
|---|---|---|---|---|
| 3 | [**Washpe — 세차 날씨·커뮤니티 앱**](src/03.Washpe/) | 2026.02 ~ 2026.04 | 1명 (기획·설계·백엔드·앱·배포 단독) | Supabase (PostgreSQL, Edge Functions/TypeScript, RLS), Cloudflare R2·Pages, Cloud Run, Flutter, Google Play·App Store 결제 |

### 3. Washpe → [`src/03.Washpe/`](src/03.Washpe/)
날씨 기반 세차 적합도(DWI) 계산, 약품 재고를 반영한 맞춤 조언, 커뮤니티, 구독 결제를 갖춘 앱. 백엔드 전체(Edge Function 14종, 마이그레이션 30개)를 1인으로 설계·운영.
**앱 시작 시 Edge Function 호출 10-12회 → 4-5회, JWT 검증 4-5회 → 1회.**

| 담당업무 | 폴더 |
|---|---|
| 1) 오프라인 우선 동기화 — Transactional Outbox, 멱등 키, Fatal/Transient 재시도, orphan 복구 | [`1.OfflineFirstSync/`](src/03.Washpe/1.OfflineFirstSync/) |
| 2) Edge Function 공통 플랫폼 — Action 라우터, 인증·CORS·요청/감사 로그 표준화, 시작 API 집계 | [`2.EdgeFunctionPlatform/`](src/03.Washpe/2.EdgeFunctionPlatform/) |
| 3) 날씨 스코어링과 DB 기반 규칙 엔진 — 격자 단위 공유 캐시, 하드코딩 분기 27개 → 규칙 테이블 | [`3.WeatherScoringAndRuleEngine/`](src/03.Washpe/3.WeatherScoringAndRuleEngine/) |
| 4) 구독 결제 서버 검증 — 영수증 재검증, RTDN(OIDC)·SNv2(JWS 체인) 웹훅, DB 멱등 | [`4.SubscriptionVerification/`](src/03.Washpe/4.SubscriptionVerification/) |

---

## 관통하는 설계 원칙

프로젝트마다 형태는 다르지만 같은 원칙이 반복됩니다.

- **변하는 것은 설정으로, 변하지 않는 것은 상위 계층으로.** 스케줄러의 2계층 상속(1-1), 오케스트레이션의 Base Framework(2-2), 조언 규칙의 테이블화(3-3) — 같은 결정입니다. 결과는 "신규 브랜드·도메인 추가 = 배포 없이 등록".
- **실패를 격리하고, 되돌릴 수 있게.** 큐·워커 서버 분리(1-3), Step 단위 격리와 Saga 보상(2-1), 이중 멱등성·분산 락·DLQ 재처리(2-3), Outbox 멱등 재전송·결제 웹훅 멱등(3-1, 3-4).
- **운영자가 개발자 없이 대응할 수 있게.** 3단 운영 제어(1-3), 관리 콘솔(1-4), 실시간 상태 허브(2-4).
- **원인까지 파고든다.** 조회 데드락의 파라미터 타입 불일치(1-4), .NET Framework 압축 포맷 브리지(1-4).

## 폴더 구조

```
Portfolio.sln
src/
  01.UnifiedScheduler/        1.ConfigurationInheritance  2.ExecutionPipeline  3.FaultIsolationAndOperations  4.OperationsConsoleAndStabilization
  02.WmsOrchestration/        1.DistributedOrchestration  2.BaseFramework      3.FaultTolerance               4.GatewayAndAuth
  03.Washpe/                  1.OfflineFirstSync          2.EdgeFunctionPlatform         3.WeatherScoringAndRuleEngine  4.SubscriptionVerification
```

## 라이선스
[MIT](LICENSE)

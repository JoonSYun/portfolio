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

## 프로젝트 (에스엘케이 IT솔루션팀, 2025.02 ~ 재직중)

| # | 프로젝트 | 기간 | 규모 | 기술 스택 |
|---|---|---|---|---|
| 1 | [**통합 스케줄러 플랫폼**](src/01.UnifiedScheduler/) | 2026.04 ~ 2026.07 | 1명 (설계·개발·운영 단독) | .NET 8, ASP.NET Core, Hangfire, Cronos, Dapper, EF Core, MS SQL Server, Serilog + Seq, React, JWT, Polly |
| 2 | [**외부 연동 플랫폼 및 사방넷 커넥터**](src/02.IntegrationPlatform/) | 2026.08 ~ 진행중 | 1명 (단독 설계·개발) | C#, ASP.NET Core, EF Core, MS SQL Server, REST API, JSON, Outbox 패턴 |
| 3 | [**이비즈웨이 ERP 연동 인터페이스**](src/03.ErpInterface/) | 2026.06 ~ 2026.08 | 2명 (연동 인터페이스 서버·WMS 연동 모듈 담당) | C#, ASP.NET(ASMX/SOAP), WinForms, Infragistics, MS SQL Server, ASP.NET Core, React |
| 4 | [**물류센터 정산 시스템**](src/04.SettlementSystem/) | 2025.04 ~ 2025.11 | 2명 | C#, WinForms, Infragistics, MS SQL Server |
| 5 | [**WMS 오케스트레이션 플랫폼 (MSA 전환)**](src/05.WmsOrchestration/) | 2025.05 ~ 2026.01 | 3명 (오케스트레이션 레이어·MQ 전체 설계·구현 담당) | .NET 8, ASP.NET Core Web API, MassTransit, RabbitMQ, Redis, EF Core, Dapper, PostgreSQL, SQL Server, Hangfire, YARP, Polly, SignalR, JWT |

### 1. 통합 스케줄러 플랫폼 → [`src/01.UnifiedScheduler/`](src/01.UnifiedScheduler/)
서비스별로 흩어져 운영되던 레거시 스케줄러를 단일 백엔드와 단일 관리 콘솔로 통합. 스케줄 설정을 코드에서 DB로 분리해, 설정 변경만으로 서비스가 확장되는 플랫폼으로 재설계.
**운영 전환 3개월 만에 75개 도메인 / 34개 브랜드로 확장, 누적 66만 건 성공률 99.95% 무중단 운영.**

| 담당업무 | 폴더 |
|---|---|
| 1) 설정으로 확장하는 구조 — 2계층 상속 설정 모델, 280건 브랜드 설정 무배포 운영 | [`1.ConfigurationInheritance/`](src/01.UnifiedScheduler/1.ConfigurationInheritance/) |
| 2) 표준화된 실행 파이프라인과 공통 프레임워크 — 8계층 파이프라인, 공통 Job 베이스, 파생 Job 69종 | [`2.ExecutionPipeline/`](src/01.UnifiedScheduler/2.ExecutionPipeline/) |
| 3) 장애 격리와 운영 자율화 — 큐·워커·알림 서버 분리, 3단 운영 제어 | [`3.FaultIsolationAndOperations/`](src/01.UnifiedScheduler/3.FaultIsolationAndOperations/) |
| 4) 운영 도구화와 안정화 — React 관리 콘솔, 조회 데드락·레거시 포맷 인시던트 | [`4.OperationsConsoleAndStabilization/`](src/01.UnifiedScheduler/4.OperationsConsoleAndStabilization/) |

### 2. 외부 연동 플랫폼 및 사방넷 커넥터 → [`src/02.IntegrationPlatform/`](src/02.IntegrationPlatform/)
외부 시스템 연동을 전담하는 연동 플랫폼 신규 설계. 연동처마다 다른 것과 공통된 것을 분리해 신규 커넥터는 고유 로직만 구현하면 되는 구조. 첫 커넥터로 사방넷 연동 모듈 개발.

| 담당업무 | 폴더 |
|---|---|
| 1) Core / 커넥터 2계층 구조 | [`1.CoreConnectorLayers/`](src/02.IntegrationPlatform/1.CoreConnectorLayers/) |
| 2) 업무 규칙의 설정화 — 단일값·목록·코드매핑표 3종 스키마, 브랜드·센터·서비스 스냅샷 병합 | [`2.RuleConfiguration/`](src/02.IntegrationPlatform/2.RuleConfiguration/) |
| 3) 운영자 중심의 결과 모델 — 건 단위 + 단계별 판정 누적 | [`3.ResultModel/`](src/02.IntegrationPlatform/3.ResultModel/) |
| 4) 시스템 간 정합성 보장 — WMS 우선 순서 고정, Outbox | [`4.Consistency/`](src/02.IntegrationPlatform/4.Consistency/) |

### 3. 이비즈웨이 ERP 연동 인터페이스 → [`src/03.ErpInterface/`](src/03.ErpInterface/)
브랜드사 ERP와 자사 WMS 간 마스터·입출고·온라인 주문·반품 양방향 연동 인터페이스 신규 구축. **총 24종 인터페이스 설계, 5개 브랜드 운영 전환 완료.**

| 담당업무 | 폴더 |
|---|---|
| 1) 반복 관심사의 공통화 — 조회 9종·수신 15종의 검증·복원·예외 변환을 상위 Core 로 | [`1.CoreAbstraction/`](src/03.ErpInterface/1.CoreAbstraction/) |
| 2) 상대 시스템에 의존하지 않는 검증 환경 — 스키마 기반 더미데이터 생성기, 검증기/전송기 분리 | [`2.Verification/`](src/03.ErpInterface/2.Verification/) |
| 3) WMS 연동과 자동화 연계 — 브랜드별 배송 정책 화면, 반품 회수 지시 스케줄러 등록 | [`3.WmsIntegration/`](src/03.ErpInterface/3.WmsIntegration/) |

### 4. 물류센터 정산 시스템 → [`src/04.SettlementSystem/`](src/04.SettlementSystem/)
엑셀 수작업으로 처리하던 물류센터 정산 업무 시스템화. 근태 입력부터 매출·비용 산정, 실행원가명세서·청구내역서 발행까지 end-to-end 정산 파이프라인 구축.

| 담당업무 | 폴더 |
|---|---|
| 1) 복잡한 정산 규칙의 데이터 모델화 — 40개 이상 비용 항목, 화면별 전용 프로시저 | [`1.DataModel/`](src/04.SettlementSystem/1.DataModel/) |
| 2) 수작업의 자동화 — 근태 집계, 인건비 자동 산정, 물동량 연동 매출 계산 | [`2.Automation/`](src/04.SettlementSystem/2.Automation/) |
| 3) 현업 생산성 도구 — 엑셀 복사/붙여넣기 엔진, Import/Export 공통 컴포넌트 | [`3.ProductivityTools/`](src/04.SettlementSystem/3.ProductivityTools/) |

### 5. WMS 오케스트레이션 플랫폼 (MSA 전환) → [`src/05.WmsOrchestration/`](src/05.WmsOrchestration/)
모놀리식 API를 MSA로 전환. WMS와 다수의 외부 쇼핑몰·물류 시스템 간 대량 데이터 연동을 위한 메시지 기반 오케스트레이션 플랫폼 구축.
**타임아웃·네트워크 장애·DB 락이 겹치는 상황에서도 서버 응답 장애 0건.**

| 담당업무 | 폴더 |
|---|---|
| 1) 분산 오케스트레이션 설계 — Job·Chunk·Step 3계층, Saga State Machine | [`1.DistributedOrchestration/`](src/05.WmsOrchestration/1.DistributedOrchestration/) |
| 2) 확장을 위한 Base Framework — 공통 상위 클래스, Attribute 바인딩 엔진 | [`2.BaseFramework/`](src/05.WmsOrchestration/2.BaseFramework/) |
| 3) 분산 환경의 장애 내성 — 이중 멱등성, Polly, Redis 분산 락, Outbox/Inbox, DLQ | [`3.FaultTolerance/`](src/05.WmsOrchestration/3.FaultTolerance/) |
| 4) 게이트웨이 및 인증 인프라 — YARP, 요청량 제한, SignalR, 이원화 DB, JWT·다중 테넌트 | [`4.GatewayAndAuth/`](src/05.WmsOrchestration/4.GatewayAndAuth/) |

---

## 관통하는 설계 원칙

프로젝트마다 형태는 다르지만 같은 원칙이 반복됩니다.

- **변하는 것은 설정으로, 변하지 않는 것은 상위 계층으로.** 스케줄러의 2계층 상속(1-1), 연동 플랫폼의 규칙 설정화(2-2), ERP 인터페이스의 Core 추상화(3-1), 오케스트레이션의 Base Framework(5-2) — 전부 같은 결정입니다. 결과는 "신규 브랜드·도메인·커넥터 추가 = 배포 없이 등록".
- **실패를 격리하고, 되돌릴 수 있게.** 큐·워커 서버 분리(1-3), WMS 우선 순서와 Outbox(2-4), Step 단위 격리와 Saga 보상(5-1), 이중 멱등성·분산 락·DLQ 재처리(5-3).
- **운영자가 개발자 없이 대응할 수 있게.** 3단 운영 제어(1-3), 건 단위·단계별 판정 알림(2-3), 관리 콘솔(1-4), 더미데이터 관리 페이지(3-2), 실시간 상태 허브(5-4).
- **원인까지 파고든다.** 조회 데드락의 파라미터 타입 불일치(1-4), .NET Framework 압축 포맷 브리지(1-4), 명세에 없는 200+FAIL 응답 검증(2-1).

## 폴더 구조

```
Portfolio.sln
src/
  01.UnifiedScheduler/        1.ConfigurationInheritance  2.ExecutionPipeline  3.FaultIsolationAndOperations  4.OperationsConsoleAndStabilization
  02.IntegrationPlatform/     1.CoreConnectorLayers       2.RuleConfiguration  3.ResultModel                  4.Consistency
  03.ErpInterface/            1.CoreAbstraction           2.Verification       3.WmsIntegration
  04.SettlementSystem/        1.DataModel                 2.Automation         3.ProductivityTools
  05.WmsOrchestration/        1.DistributedOrchestration  2.BaseFramework      3.FaultTolerance               4.GatewayAndAuth
```

## 라이선스
[MIT](LICENSE)

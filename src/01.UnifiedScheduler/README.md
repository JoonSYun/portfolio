# [프로젝트 1] 통합 스케줄러 플랫폼

| | |
|---|---|
| 기간 | 2026.04 ~ 2026.07 |
| 규모 | 1명 (설계·개발·운영 단독) |
| 기술 스택 | .NET 8, ASP.NET Core, Hangfire, Cronos, Dapper, EF Core, MS SQL Server, Serilog + Seq, React, JWT, Polly |

**개요** — 서비스별로 흩어져 운영되던 레거시 스케줄러를 단일 백엔드와 단일 관리 콘솔로 통합.
스케줄 설정을 코드에서 DB로 분리해, 기능 추가가 아닌 설정 변경만으로 서비스가 확장되는 플랫폼으로 재설계.
운영 전환 3개월 만에 75개 도메인 / 34개 브랜드로 확장, 누적 70만 건 성공률 100%로 무중단 운영 중.

## 담당업무 ↔ 코드

### 1) 설정으로 확장하는 구조 → [`1.ConfigurationInheritance/`](1.ConfigurationInheritance/)
> 변하는 것(브랜드·주기·파라미터)은 도메인 기본값과 브랜드별 오버라이드의 2계층 상속 모델로 설정화하고, 변하지 않는 실행 규칙은 상위 계층으로 분리. 신규 브랜드·도메인 대응이 배포 없이 설정 등록만으로 완료되며 현재 280건의 브랜드별 설정을 무배포로 운영

| 파일 | 내용 |
|---|---|
| [`DomainSchedule.cs`](1.ConfigurationInheritance/DomainSchedule.cs) | 상위 계층 — 도메인 기본값(변하지 않는 실행 규칙) |
| [`BrandScheduleOverride.cs`](1.ConfigurationInheritance/BrandScheduleOverride.cs) | 하위 계층 — 모든 필드 nullable, null = 상속 |
| [`ScheduleConfigResolver.cs`](1.ConfigurationInheritance/ScheduleConfigResolver.cs) | 병합 규칙 (필드 단위 상속 + 파라미터 얕은 병합), 발사 시 브랜드 fan-out |
| [`ScheduleConfigRepository.cs`](1.ConfigurationInheritance/ScheduleConfigRepository.cs) · [`Sql/schedule_config.sql`](1.ConfigurationInheritance/Sql/schedule_config.sql) | 코드 → DB 로 옮긴 단일 진실 소스 (Dapper) |

### 2) 표준화된 실행 파이프라인과 공통 프레임워크 → [`2.ExecutionPipeline/`](2.ExecutionPipeline/)
> 등록부터 알림까지 이어지는 실행 흐름을 하나의 파이프라인으로 표준화하고, 로깅·검증·상태 전이·타임아웃·실패 마감 등 실행 라이프사이클 전 구간을 공통 Job 베이스로 통일. 파생 Job 69종은 비즈니스 로직만 구현하도록 하여 확장 비용 최소화

| 파일 | 내용 |
|---|---|
| [`Core/JobBase.cs`](2.ExecutionPipeline/Core/JobBase.cs) | **공통 Job 베이스 (원문)**. 단일 try/catch 로 페이로드 역직렬화 → DataAnnotations 검증 → ENQUEUED→RUNNING → shutdown+timeout linked token → 비즈니스 → SUCCESS/FAILED → 알림. 마감·알림 단계만 예외를 swallow 하고 원본 예외는 `ExceptionDispatchInfo` 로 stack 보존 rethrow |
| [`Core/Dispatcher.cs`](2.ExecutionPipeline/Core/Dispatcher.cs) | 발사·필터·적재. 도메인 cron tick 1회 → 활성 브랜드 fan-out → 브랜드 시간대 cron(OR) narrowing → 활성 잡 중복 검사(1회 SELECT) → Hangfire enqueue. cron/수동 3개 진입점이 같은 경로를 공유 |
| [`Core/ScheduleSyncJob.cs`](2.ExecutionPipeline/Core/ScheduleSyncJob.cs) | 등록·동기화. DB 스케줄(원본) → Hangfire RecurringJob(파생) 매분 복제 — UPD_DT 체크포인트 + 등록셋↔활성셋 양방향 reconcile |
| [`Core/DeprecationSweeper.cs`](2.ExecutionPipeline/Core/DeprecationSweeper.cs) · [`Infrastructure/Hangfire/`](2.ExecutionPipeline/Infrastructure/Hangfire/) | 마감 보조. 큐 대기 한도(DEPRECATE_SEC)·실행 한도(TIMEOUT_SEC) 초과 잡을 필터(실행 직전)와 스위퍼(매분 SQL 스캔) 두 지점에서 폐기 |
| [`Core/NotificationJob.cs`](2.ExecutionPipeline/Core/NotificationJob.cs) | 알림. 시스템/브랜드 수신자 그룹을 게이트·본문·발송 모두 독립 처리, timeout 합성 |
| [`Infrastructure/JobLogRepository.cs`](2.ExecutionPipeline/Infrastructure/JobLogRepository.cs) | 상태 전이 저장소. 모든 전이가 from-status 가드 + 행수 반환으로 멱등, `MarkRunningOutcome` 으로 정상 race(DEPRECATED)와 정합성 깨짐을 구분 |
| [`Core/DomainJobRegistry.cs`](2.ExecutionPipeline/Core/DomainJobRegistry.cs) · [`Core/IDomainJob.cs`](2.ExecutionPipeline/Core/IDomainJob.cs) | DB 의 JOB_TYPE(short name) → Type 매핑. 중복 이름은 기동 시 fail-loud |
| [`Jobs/DBReindexJob.cs`](2.ExecutionPipeline/Jobs/DBReindexJob.cs) · [`Jobs/MailTestJob.cs`](2.ExecutionPipeline/Jobs/MailTestJob.cs) | 파생 Job 예시 — `ExecuteCoreAsync` 하나 / 운영·테스트 경로와 시스템·업무 예외 라우팅 |
| [`Models/`](2.ExecutionPipeline/Models/) · [`Infrastructure/`](2.ExecutionPipeline/Infrastructure/) | 위 파일들이 참조하는 페이로드(`JobArgs`)·상태(`JobStatus`)·예외·옵션·로깅 확장·Polly SQL 재시도 파이프라인 |

> 이 폴더의 코드는 **실제 운영 중인 원문**입니다 (회사·브랜드·DB 카탈로그 이름만 일반화). 8계층 파이프라인은 위 파일에 이렇게 대응합니다 —
> 등록·동기화 `ScheduleSyncJob` / 발사·필터·적재 `Dispatcher` + Hangfire 필터 / 실행·마감 `JobBase` + `JobLogRepository` / 알림 `NotificationJob`.

### 3) 장애 격리와 운영 자율화 → [`3.FaultIsolationAndOperations/`](3.FaultIsolationAndOperations/)
> 큐·워커·알림을 서버 단위로 분리해 한 작업의 실패가 다른 브랜드나 플랫폼 전체로 전이되지 않도록 차단. 도메인·그룹·브랜드·큐 어느 수준에서든 즉시 차단·검증·일시정지가 가능한 3단 운영 제어로 개발자 개입 없는 운영자 직접 대응 환경 확보

| 파일 | 내용 |
|---|---|
| [`WorkerTopology.cs`](3.FaultIsolationAndOperations/WorkerTopology.cs) | 큐 = 워커 서버 단위 분리, 알림 전용 노드 |
| [`HangfireNotifier.cs`](2.ExecutionPipeline/Infrastructure/Notifiers/HangfireNotifier.cs) · [`NotificationJob.cs`](2.ExecutionPipeline/Core/NotificationJob.cs) (2.ExecutionPipeline 원문) | 알림을 실행 워커와 프로세스 수준에서 격리 — 실행 워커는 알림 큐에 enqueue 만 하고 즉시 리턴, 발송은 알림 전용 노드의 `NotificationJob` |
| [`OperationalControl/OperationalGate.cs`](3.FaultIsolationAndOperations/OperationalControl/OperationalGate.cs) | 3단 제어: 전역 일시정지 / 도메인·그룹·브랜드·큐 차단 / 검증 모드 + 감사 로그 |
| [`OperationalControl/OperationalControlController.cs`](3.FaultIsolationAndOperations/OperationalControl/OperationalControlController.cs) | 운영자용 제어 API (JWT Operator 역할) |

### 4) 운영 도구화와 안정화 → [`4.OperationsConsoleAndStabilization/`](4.OperationsConsoleAndStabilization/)
> React 관리 콘솔로 실행 이력·예정·수동 실행·DB 헬스를 한 화면에 통합해 현업 상시 도구로 정착. 조회 데드락·레거시 포맷 호환 등 난제를 원인까지 추적·해결하고 인시던트 문서로 사내 공유

| 파일 | 내용 |
|---|---|
| [`Api/ConsoleControllers.cs`](4.OperationsConsoleAndStabilization/Api/ConsoleControllers.cs) | 실행 이력 · 실행 예정(Cronos) · 수동 실행 · DB 헬스 |
| [`AdminConsole/ExecutionHistoryPage.tsx`](4.OperationsConsoleAndStabilization/AdminConsole/ExecutionHistoryPage.tsx) · [`api.ts`](4.OperationsConsoleAndStabilization/AdminConsole/api.ts) | React 콘솔 (발췌) |
| [`Incidents/QueryDeadlock_ParameterTypeMismatch.md`](4.OperationsConsoleAndStabilization/Incidents/QueryDeadlock_ParameterTypeMismatch.md) · [`History/ExecutionHistoryRepository.cs`](4.OperationsConsoleAndStabilization/History/ExecutionHistoryRepository.cs) | 조회 데드락 — 파라미터 타입 불일치로 인덱스 미사용, 원인 추적과 수정 |
| [`Incidents/LegacyCompressionBridge.cs`](4.OperationsConsoleAndStabilization/Incidents/LegacyCompressionBridge.cs) | .NET Framework 전용 압축 포맷 → 브리지 프로세스로 무수정 수용 |
| [`Logging/SerilogSeqSetup.cs`](4.OperationsConsoleAndStabilization/Logging/SerilogSeqSetup.cs) | Serilog → Seq 구조적 로깅 |

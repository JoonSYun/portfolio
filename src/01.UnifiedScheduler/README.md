# [프로젝트 1] 통합 스케줄러 플랫폼

| | |
|---|---|
| 기간 | 2026.04 ~ 2026.07 |
| 규모 | 1명 (설계·개발·운영 단독) |
| 기술 스택 | .NET 8, ASP.NET Core, Hangfire, Cronos, Dapper, EF Core, MS SQL Server, Serilog + Seq, React, JWT, Polly |

**개요** — 서비스별로 흩어져 운영되던 레거시 스케줄러를 단일 백엔드와 단일 관리 콘솔로 통합.
스케줄 설정을 코드에서 DB로 분리해, 기능 추가가 아닌 설정 변경만으로 서비스가 확장되는 플랫폼으로 재설계.
운영 전환 3개월 만에 75개 도메인 / 34개 브랜드로 확장, 누적 66만 건 성공률 99.95%로 무중단 운영 중.

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
| [`PipelineStages.cs`](2.ExecutionPipeline/PipelineStages.cs) | 8계층 정의: 등록·동기화·발사·필터·적재·실행·마감·알림 |
| [`SchedulePipeline.cs`](2.ExecutionPipeline/SchedulePipeline.cs) | 1~5단계. **브랜드 fan-out 을 발사 단계에** 배치 (등록 비용 = 도메인 수) |
| [`JobBase.cs`](2.ExecutionPipeline/JobBase.cs) | 공통 Job 베이스: 로그 부착 → 검증 → 상태 전이 → 타임아웃 전파 → 실패 마감 → 알림 |
| [`JobExecutionState.cs`](2.ExecutionPipeline/JobExecutionState.cs) | 상태 전이 규칙 강제 |
| [`Jobs/ReturnPickupInstructionJob.cs`](2.ExecutionPipeline/Jobs/ReturnPickupInstructionJob.cs) | 파생 Job 예시 — 검증 하나, 비즈니스 로직 하나 |

### 3) 장애 격리와 운영 자율화 → [`3.FaultIsolationAndOperations/`](3.FaultIsolationAndOperations/)
> 큐·워커·알림을 서버 단위로 분리해 한 작업의 실패가 다른 브랜드나 플랫폼 전체로 전이되지 않도록 차단. 도메인·그룹·브랜드·큐 어느 수준에서든 즉시 차단·검증·일시정지가 가능한 3단 운영 제어로 개발자 개입 없는 운영자 직접 대응 환경 확보

| 파일 | 내용 |
|---|---|
| [`WorkerTopology.cs`](3.FaultIsolationAndOperations/WorkerTopology.cs) | 큐 = 워커 서버 단위 분리, 알림 전용 노드 |
| [`NotificationWorker.cs`](3.FaultIsolationAndOperations/NotificationWorker.cs) | 알림을 실행 워커와 프로세스 수준에서 격리 |
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

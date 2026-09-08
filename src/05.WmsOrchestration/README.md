# [프로젝트 5] WMS 오케스트레이션 플랫폼 (MSA 전환)

| | |
|---|---|
| 기간 | 2025.05 ~ 2026.01 |
| 규모 | 3명 (오케스트레이션 레이어 · MQ 전체 설계·구현 담당) |
| 기술 스택 | .NET 8, ASP.NET Core Web API, MassTransit, RabbitMQ, Redis, EF Core, Dapper, PostgreSQL, SQL Server, Hangfire, YARP, Polly, SignalR, JWT |

**개요** — 모놀리식 API 시스템을 MSA로 전환. WMS와 다수의 외부 쇼핑몰·물류 시스템 간 대량 데이터 연동을 위한 메시지 기반 오케스트레이션 플랫폼 구축.
타임아웃·네트워크 장애·DB 락이 겹치는 상황에서도 서버 응답 장애 0건 달성.

## 담당업무 ↔ 코드

### 1) 분산 오케스트레이션 설계 → [`1.DistributedOrchestration/`](1.DistributedOrchestration/)
> 대량 연동 요청을 Job·Chunk·Step 3계층으로 분할해 병렬 처리, 각 단계의 성공·실패·보상 흐름을 Saga State Machine으로 자동 제어

> 이 폴더의 Saga State Machine 4종과 상태·이벤트·활동 클래스는 **실제 운영 중인 원문**입니다 (회사 접두사만 제거). 리포지토리·DbContext 계층은 제외했습니다.
>
> ```
> JobSaga            Initial → Processing → ChunkCompleted → Completed                 (JobId 상관)
> JobProcessSaga     Initial → Processing → Completed | Failed                         (JobId 상관, 청크 완료 후 순차 후처리)
> ChunkStepSaga      Initial → Processing → WaitingCompensation → Completed | Failed   (ChunkId 상관)
> CompensationSaga   Initial → Compensating → Completed | Failed                       (CompensationId 상관, 역순 1개씩)
> ```

| 파일 | 내용 |
|---|---|
| [`Saga/StateMachine/JobSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/JobSaga.cs) | **Job 수준**. 병렬 청크 결과가 동시에 들어와도 Saga 낙관적 동시성 충돌이 없도록, 상태를 직접 갱신하지 않고 DB 갱신 커맨드를 발행 → `ProjectionConsumer/` 가 DB 락으로 처리 |
| [`Saga/StateMachine/ChunkStepSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/ChunkStepSaga.cs) | **Chunk → Step 순차 실행**. SUCCESS / PARTIAL_SUCCESS / FAILED 3분기, 부분 실패는 실패 식별자만·업무 실패는 이전 Step 전부 보상 Saga 를 띄우고 **끝까지 진행** 후 `WaitingCompensation` 에서 최종 상태 확정 |
| [`Saga/StateMachine/ChunkStepCompensationSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/ChunkStepCompensationSaga.cs) | **보상**. 실패 Step 이전을 역순으로 하나씩, 보상 실패 시 완료/건너뜀 인덱스를 담은 알림 발행 후 종료 |
| [`Saga/StateMachine/JobProcessSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/JobProcessSaga.cs) | **Job 후처리**. 모든 청크 완료 후 Process 플랜을 순차 실행, 실패 시 Job 최종 상태를 FAILED 로 |
| [`Saga/State/`](1.DistributedOrchestration/Saga/State/) | Saga 인스턴스 상태 + EF Core `SagaClassMap` (버전 컬럼 낙관적 동시성) |
| [`Saga/Event/`](1.DistributedOrchestration/Saga/Event/) | Job · Chunk · Step · 보상 이벤트/커맨드 계약과 상관 스코프 인터페이스 (`IJobScope` / `IChunkScope` / `ICompensationScope`) |
| [`Saga/Activity/`](1.DistributedOrchestration/Saga/Activity/) | Saga 안에서 DB 를 만지는 부분만 Activity 로 분리 — Step/Process 플랜 적재, 결과 갱신 (Polly 재시도) |
| [`Saga/Helper/DomainEventFactory.cs`](1.DistributedOrchestration/Saga/Helper/DomainEventFactory.cs) · [`EventMappingExtensions.cs`](1.DistributedOrchestration/Saga/Helper/EventMappingExtensions.cs) | Step 타입 문자열 → 이벤트 타입 리플렉션 생성. Saga 가 도메인을 몰라도 다음 Step 을 발행 |
| [`Saga/ProjectionConsumer/`](1.DistributedOrchestration/Saga/ProjectionConsumer/) | Saga 가 발행한 갱신 커맨드를 받아 DB 를 갱신하는 Consumer 들 — `JobConsumerBase` 파생 예시 |

### 2) 확장을 위한 Base Framework → [`2.BaseFramework/`](2.BaseFramework/)
> 로깅·중복 방지·상태 알림 등 공통 로직을 상위 클래스로 추상화, Attribute만 추가하면 메시지 큐가 자동 등록되는 바인딩 엔진 개발로 신규 도메인은 비즈니스 로직만 작성하면 되는 구조 확립

> 이 폴더의 Base 클래스 5종과 바인딩 엔진은 **실제 운영 중인 원문**입니다 (회사 접두사만 제거).

| 파일 | 내용 |
|---|---|
| [`Base/ChunkStepConsumerBase.cs`](2.BaseFramework/Base/ChunkStepConsumerBase.cs) | **Step Consumer 베이스**. 정상/보상 모드 분기, 페이로드 fail-fast 역직렬화, 정상·보상 각각의 멱등성 게이트, 결과 이벤트 발행 표준화, 부분 실패 식별자 필터(`GetValidPayloads`/`GetFailedPayloads`), 외부 API 응답 코드 → Step 결과 변환 |
| [`Base/JobConsumerBase.cs`](2.BaseFramework/Base/JobConsumerBase.cs) · [`Base/ChunkConsumerBase.cs`](2.BaseFramework/Base/ChunkConsumerBase.cs) | Job / Chunk 수준 Consumer 베이스 — 로깅 · 멱등성 · 보상 훅 |
| [`Base/JobProcessConsumerBase.cs`](2.BaseFramework/Base/JobProcessConsumerBase.cs) | Job 후처리 Consumer 베이스 — 예외를 삼키고 성공/실패 이벤트로 Saga 에 보고 |
| [`Base/NTISConsumerBase.cs`](2.BaseFramework/Base/NTISConsumerBase.cs) | ChunkStepConsumerBase 를 한 번 더 상속한 도메인 베이스 — 프로시저 실행 도메인은 `ExecuteProcedureAsync` 하나만 |
| [`Messaging/MassTransitAutoBindAttribute.cs`](2.BaseFramework/Messaging/MassTransitAutoBindAttribute.cs) · [`MassTransitAutoBinder.cs`](2.BaseFramework/Messaging/MassTransitAutoBinder.cs) | Attribute 바인딩 엔진 — DTO 의 `[MassTransitAutoBind]` 와 Consumer 의 `[MassTransitConsumerAutoBind]` 를 스캔해 Exchange·Queue·prefetch·동시성·재시도·우선순위 자동 구성 |
| [`Messaging/MassTransitExtensions.cs`](2.BaseFramework/Messaging/MassTransitExtensions.cs) | Program.cs 진입점 — Saga 4종 EF 리포지토리, EF Outbox, RabbitMQ 호스트, 관찰자 |
| [`Consumers/WarehousesReqInsertConsumer.cs`](2.BaseFramework/Consumers/WarehousesReqInsertConsumer.cs) · [`Consumers/NTISWarehouseMasterSyncConsumer.cs`](2.BaseFramework/Consumers/NTISWarehouseMasterSyncConsumer.cs) | 신규 도메인 예시 — 비즈니스 호출과 멱등성 조회만 / 프로시저 호출 한 줄만 |
| [`Service/`](2.BaseFramework/Service/) · [`Infrastructure/Redis/`](2.BaseFramework/Infrastructure/Redis/) · [`Model/`](2.BaseFramework/Model/) | 베이스가 참조하는 멱등성 서비스, Redis 락/캐시 매니저, 응답 모델 |

### 3) 분산 환경의 장애 내성 → [`3.FaultTolerance/`](3.FaultTolerance/)
> 이중 멱등성 체크, Polly 재시도 정책, Redis 분산 락, Outbox/Inbox 패턴 적용으로 장애 상황에서도 무중단 운영 달성

| 파일 | 내용 |
|---|---|
| [`DoubleIdempotency.cs`](3.FaultTolerance/DoubleIdempotency.cs) | MessageId + 비즈니스 키 이중 체크 (Redis SET NX) |
| [`RetryPolicies.cs`](3.FaultTolerance/RetryPolicies.cs) | 메시지 층(MassTransit 재시도·지연 재전달) + 호출 층(Polly 타임아웃·재시도·서킷) |
| [`RedisDistributedLock.cs`](3.FaultTolerance/RedisDistributedLock.cs) | 소유 토큰 검사 Lua 해제, TTL 갱신 |
| [`OutboxInboxAndDeadLetter.cs`](3.FaultTolerance/OutboxInboxAndDeadLetter.cs) | EF Core Outbox/Inbox (Saga 와 같은 트랜잭션), DLQ 자동 재처리 |

### 4) 게이트웨이 및 인증 인프라 → [`4.GatewayAndAuth/`](4.GatewayAndAuth/)
> YARP 기반 통합 라우팅과 요청량 제한, 실시간 상태 모니터링 구축, 이원화 DB 구성과 JWT·다중 테넌트 접근 제어 구현

| 파일 | 내용 |
|---|---|
| [`GatewaySetup.cs`](4.GatewayAndAuth/GatewaySetup.cs) · [`yarp.appsettings.json`](4.GatewayAndAuth/yarp.appsettings.json) | YARP 라우팅, 테넌트별 토큰 버킷 요청량 제한 |
| [`StatusHub.cs`](4.GatewayAndAuth/StatusHub.cs) | SignalR 실시간 상태 모니터링 |
| [`DualDatabaseAndJwt.cs`](4.GatewayAndAuth/DualDatabaseAndJwt.cs) | PostgreSQL(오케스트레이션) + SQL Server(WMS 원장) 이원화, JWT · 다중 테넌트 접근 제어 |

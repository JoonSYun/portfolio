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

| 파일 | 내용 |
|---|---|
| [`Messages.cs`](1.DistributedOrchestration/Messages.cs) | Job · Chunk · Step 메시지 계약 |
| [`JobPartitioner.cs`](1.DistributedOrchestration/JobPartitioner.cs) | Job → Chunk 분할, 병렬 발행 |
| [`ChunkProcessor.cs`](1.DistributedOrchestration/ChunkProcessor.cs) | Chunk → Step 병렬 처리, Step 실패 격리, 청크 보상 |
| [`BulkSyncSaga.cs`](1.DistributedOrchestration/BulkSyncSaga.cs) | MassTransit Saga State Machine — 성공/실패/보상 전이 선언 |

### 2) 확장을 위한 Base Framework → [`2.BaseFramework/`](2.BaseFramework/)
> 로깅·중복 방지·상태 알림 등 공통 로직을 상위 클래스로 추상화, Attribute만 추가하면 메시지 큐가 자동 등록되는 바인딩 엔진 개발로 신규 도메인은 비즈니스 로직만 작성하면 되는 구조 확립

| 파일 | 내용 |
|---|---|
| [`ConsumerBase.cs`](2.BaseFramework/ConsumerBase.cs) | 로깅 · 이중 멱등성 · SignalR 상태 알림을 상위 클래스에 |
| [`QueueBindingEngine.cs`](2.BaseFramework/QueueBindingEngine.cs) | `[MessageQueue]` 스캔 → Exchange·Queue·재시도·DLQ 자동 구성 |
| [`Consumers/InventoryPushConsumer.cs`](2.BaseFramework/Consumers/InventoryPushConsumer.cs) | 신규 도메인 예시 — 비즈니스 로직만 |

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

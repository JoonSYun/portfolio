# 3. WMS 오케스트레이션 플랫폼 (MSA 전환)

| | |
|---|---|
| 기간 | 2025.05 ~ 2026.01 |
| 역할 | 오케스트레이션 계층과 메시지 큐 전체 설계, 구현 (3명) |
| 기술 스택 | .NET 8, ASP.NET Core Web API, MassTransit, RabbitMQ, Redis, EF Core, Dapper, PostgreSQL, SQL Server, Hangfire, YARP, Polly, SignalR, JWT |

> **설계 문서**: Job, Chunk, Step 3계층과 Saga 4종의 상태 전이와 보상 흐름, Consumer 베이스와 Attribute 바인딩 엔진, 층별 장애 내성 장치의 설계 이유는 [`ARCHITECTURE.md`](ARCHITECTURE.md) 에 정리했습니다.

## 배경

모놀리식 API로는 WMS와 외부 쇼핑몰, 물류 시스템 사이의 대량 데이터 연동을 견디기 어려웠습니다.

## 담당 역할 및 실행

- **설계**: 요청을 메시지로 받아 비동기로 처리하는 오케스트레이션 구조를 설계했습니다. 대량 요청을 Job, Chunk, Step 3단계로 나눠 병렬 처리하고, 각 단계의 성공, 실패, 보상 흐름을 Saga State Machine 4종으로 제어합니다.
- **구현**: 로깅, 멱등성, 결과 이벤트 발행을 Consumer 베이스 클래스에 담아 새 도메인은 업무 로직만 작성하게 했습니다. 중복 처리 방지, 재시도, 분산 락을 공통 계층에 구현해, 일시적인 장애가 나도 처리가 이어지게 했습니다.
- **인프라**: YARP 게이트웨이 라우팅, JWT 인증, SignalR 상태 알림을 구성했습니다.

## 성과

- 메시지 기반 비동기 구조와 단계별 분할 처리 설계로, 파일럿 테스트 기간 동안 타임아웃, 네트워크 장애, DB 락이 겹치는 상황에서도 서버 응답 장애 0건을 달성해 MSA 전환 가능성을 확인했습니다.

## 구조

대량 요청 하나(Job)를 여러 묶음(Chunk)으로 나눠 병렬로 처리하고, 각 묶음 안에서는 단계(Step)를 순서대로 실행합니다. 이 흐름을 Saga State Machine 4종이 제어합니다.

```
JobSaga                     Initial → Processing → ChunkCompleted → Completed                 (JobId 상관)
JobProcessSaga              Initial → Processing → Completed | Failed                         (JobId 상관, Chunk 완료 후 순차 후처리)
ChunkStepSaga               Initial → Processing → WaitingCompensation → Completed | Failed   (ChunkId 상관)
ChunkStepCompensationSaga   Initial → Compensating → Completed | Failed                       (CompensationId 상관, 역순 1개씩)
```

병렬로 도는 Chunk 의 결과가 동시에 들어와도 Saga 의 낙관적 동시성 충돌이 나지 않도록, Saga 는 상태를 직접 갱신하지 않고 DB 갱신 커맨드만 발행하며, 이 커맨드는 별도 Consumer 가 DB 락으로 처리합니다.

## 코드 안내

이 폴더의 코드는 모두 프로젝트 기간의 구현 원문입니다. 회사 접두사와 내부 서비스 이름만 일반화했고 로직은 그대로입니다. 설계가 드러나는 파일만 골라 옮겼으므로 이 폴더만으로 빌드되지는 않습니다.

### 1) 분산 오케스트레이션 · [`1.DistributedOrchestration/`](1.DistributedOrchestration/)

Saga State Machine 4종과 상태, 이벤트, Activity 클래스입니다. 리포지토리와 DbContext 계층은 제외했습니다.

| 파일 | 내용 |
|---|---|
| [`Saga/StateMachine/JobSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/JobSaga.cs) | **Job 수준**. 상태를 직접 갱신하지 않고 DB 갱신 커맨드를 발행, `ProjectionConsumer/` 가 DB 락으로 처리 |
| [`Saga/StateMachine/ChunkStepSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/ChunkStepSaga.cs) | **Chunk 안의 Step 순차 실행**. SUCCESS / PARTIAL_SUCCESS / FAILED 3분기. 부분 실패는 실패 식별자만, 업무 실패는 전체를 대상으로 이전 Step 의 보상 Saga 를 띄우고, 끝까지 진행한 뒤 `WaitingCompensation` 에서 최종 상태 확정 |
| [`Saga/StateMachine/ChunkStepCompensationSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/ChunkStepCompensationSaga.cs) | **보상**. 실패한 Step 이전의 Step 을 역순으로 하나씩 보상. 보상이 실패하면 완료/건너뜀 인덱스를 담은 알림을 발행하고 종료 |
| [`Saga/StateMachine/JobProcessSaga.cs`](1.DistributedOrchestration/Saga/StateMachine/JobProcessSaga.cs) | **Job 후처리**. 모든 Chunk 가 끝난 뒤 후처리(Process) 계획을 순차 실행 |
| [`Saga/State/`](1.DistributedOrchestration/Saga/State/) | Saga 인스턴스 상태 + EF Core `SagaClassMap` (버전 컬럼 낙관적 동시성) |
| [`Saga/Event/`](1.DistributedOrchestration/Saga/Event/) | Job, Chunk, Step, 보상 이벤트와 커맨드 계약, 상관 스코프 인터페이스 |
| [`Saga/Activity/`](1.DistributedOrchestration/Saga/Activity/) | Saga 안에서 DB 에 접근하는 부분만 Activity 로 분리 (Polly 재시도) |
| [`Saga/Helper/DomainEventFactory.cs`](1.DistributedOrchestration/Saga/Helper/DomainEventFactory.cs) · [`EventMappingExtensions.cs`](1.DistributedOrchestration/Saga/Helper/EventMappingExtensions.cs) | Step 타입 문자열에서 이벤트 타입을 리플렉션으로 생성. Saga 가 도메인을 몰라도 다음 Step 을 발행 |
| [`Saga/ProjectionConsumer/`](1.DistributedOrchestration/Saga/ProjectionConsumer/) | Saga 가 발행한 갱신 커맨드를 받아 DB 를 갱신하는 Consumer |

### 2) 확장을 위한 Base Framework · [`2.BaseFramework/`](2.BaseFramework/)

Consumer 베이스 클래스 5종과 Attribute 바인딩 엔진입니다.

| 파일 | 내용 |
|---|---|
| [`Base/ChunkStepConsumerBase.cs`](2.BaseFramework/Base/ChunkStepConsumerBase.cs) | **Step Consumer 베이스**. 정상/보상 모드 분기, 페이로드 fail-fast 역직렬화, 정상과 보상 각각의 멱등성 게이트, 결과 이벤트 발행 표준화, 부분 실패 식별자 필터, 외부 API 응답 코드를 Step 결과로 변환 |
| [`Base/JobConsumerBase.cs`](2.BaseFramework/Base/JobConsumerBase.cs) · [`Base/ChunkConsumerBase.cs`](2.BaseFramework/Base/ChunkConsumerBase.cs) | Job / Chunk 수준 Consumer 베이스 — 로깅, 멱등성, 보상 훅 |
| [`Base/JobProcessConsumerBase.cs`](2.BaseFramework/Base/JobProcessConsumerBase.cs) | Job 후처리 Consumer 베이스 — 예외를 삼키고 성공/실패 이벤트로 Saga 에 보고 |
| [`Base/NTISConsumerBase.cs`](2.BaseFramework/Base/NTISConsumerBase.cs) | 도메인 베이스 — 프로시저 실행 도메인은 `ExecuteProcedureAsync` 하나만 구현 |
| [`Messaging/MassTransitAutoBindAttribute.cs`](2.BaseFramework/Messaging/MassTransitAutoBindAttribute.cs) · [`MassTransitAutoBinder.cs`](2.BaseFramework/Messaging/MassTransitAutoBinder.cs) | Attribute 바인딩 엔진 — DTO 와 Consumer 의 Attribute 를 스캔해 Exchange, Queue, prefetch, 동시성, 재시도, 우선순위를 자동 구성 |
| [`Messaging/MassTransitExtensions.cs`](2.BaseFramework/Messaging/MassTransitExtensions.cs) | Program.cs 에서 호출하는 등록 진입점 — Saga 4종 EF 리포지토리, EF Outbox, RabbitMQ 호스트 |
| [`Consumers/WarehousesReqInsertConsumer.cs`](2.BaseFramework/Consumers/WarehousesReqInsertConsumer.cs) · [`Consumers/NTISWarehouseMasterSyncConsumer.cs`](2.BaseFramework/Consumers/NTISWarehouseMasterSyncConsumer.cs) | 새 도메인 예시 — 업무 호출과 멱등성 조회만 / 프로시저 호출 한 줄만 |
| [`Service/`](2.BaseFramework/Service/) · [`Infrastructure/Redis/`](2.BaseFramework/Infrastructure/Redis/) · [`Model/`](2.BaseFramework/Model/) | 베이스가 참조하는 멱등성 서비스, Redis 락/캐시 매니저, 응답 모델 |

### 3) 분산 환경의 장애 내성

중복 메시지, 일시적 장애, 동시 경합에 대비한 장치입니다. 별도 폴더 없이 1번과 2번 폴더의 코드에 들어 있습니다.

| 파일 | 내용 |
|---|---|
| [`JobIdempotencyService.cs`](2.BaseFramework/Service/JobIdempotencyService.cs) · [`ChunkIdempotencyService.cs`](2.BaseFramework/Service/ChunkIdempotencyService.cs) · [`IdempotencyService.cs`](2.BaseFramework/Service/IdempotencyService.cs) | **중복 처리 방지**. Job 과 Chunk 의 현재 상태를 조회해 이미 처리된 메시지를 걸러 내고, Step 은 문서 번호와 페이지 단위로 남긴 처리 결과를 보고 다시 실행할지 정함 |
| [`RetryPolicyFactory.cs`](1.DistributedOrchestration/Infrastructure/Factory/RetryPolicyFactory.cs) · [`MassTransitAutoBinder.cs`](2.BaseFramework/Messaging/MassTransitAutoBinder.cs) | **재시도**. 호출 층은 용도별 Polly 재시도 정책(상태 갱신, 조회, HTTP 조회, Redis), 메시지 층은 DTO 의 Attribute 에 적은 횟수와 간격으로 MassTransit 이 재시도 |
| [`RedisLockManager.cs`](2.BaseFramework/Infrastructure/Redis/RedisLockManager.cs) · [`RedisFailurePolicy.cs`](2.BaseFramework/Infrastructure/Redis/RedisFailurePolicy.cs) | **분산 락**. 키가 없을 때만 쓰는 방식(SET NX)과 만료 시간으로 잠그고, Redis 장애 시 실패로 처리할지 통과시킬지를 정책으로 선택 |
| [`MassTransitExtensions.cs`](2.BaseFramework/Messaging/MassTransitExtensions.cs) · [`Saga/State/`](1.DistributedOrchestration/Saga/State/) | **메시지 유실과 동시 갱신 방지**. EF Core Outbox 로 DB 저장과 메시지 발행을 한 트랜잭션에 묶고, Saga 상태는 버전 컬럼으로 낙관적 동시성 제어 |

### 4) 게이트웨이와 인증 · [`4.GatewayAndAuth/`](4.GatewayAndAuth/)

게이트웨이 라우팅과 인증 구성입니다.

| 파일 | 내용 |
|---|---|
| [`Gateway/Program.cs`](4.GatewayAndAuth/Gateway/Program.cs) · [`Gateway/appsettings.json`](4.GatewayAndAuth/Gateway/appsettings.json) | YARP 리버스 프록시. 오케스트레이션 API, SignalR 허브, WMS 서비스로 경로를 나눠 전달하고 상태 점검과 요청 로그를 구성 |
| [`Gateway/RateLimitingServiceCollectionExtensions.cs`](4.GatewayAndAuth/Gateway/RateLimitingServiceCollectionExtensions.cs) · [`ApiRateLimitOptions.cs`](4.GatewayAndAuth/Gateway/ApiRateLimitOptions.cs) | 게이트웨이 앞단의 요청량 제한. 설정값으로 조정하는 고정 윈도 방식이고 한도를 넘으면 429 로 거절 |
| [`Auth/JwtTokenService.cs`](4.GatewayAndAuth/Auth/JwtTokenService.cs) | JWT 발급. SignalR 사용자 식별과 역할 기반 인가에 쓰는 클레임을 담음 |
| [`OrchestrationHub.cs`](1.DistributedOrchestration/Infrastructure/SignalR/OrchestrationHub.cs) · [`NotificationService.cs`](1.DistributedOrchestration/Service/NotificationService.cs) (1.DistributedOrchestration) | SignalR 상태 알림. 사용자, 역할, 센터, 회사 단위로 처리 결과를 전달 |

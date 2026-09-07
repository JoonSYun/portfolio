# [프로젝트 2] 외부 연동 플랫폼 및 사방넷 커넥터

| | |
|---|---|
| 기간 | 2026.08 ~ 진행중 |
| 규모 | 1명 (단독 설계·개발) |
| 기술 스택 | C#, ASP.NET Core, EF Core, MS SQL Server, REST API, JSON, Outbox 패턴 |

**개요** — 외부 시스템 연동을 전담하는 연동 플랫폼 신규 설계.
연동처마다 다른 것(프로토콜·업무 흐름)과 공통된 것(설정·인증·로깅·예외·전송 보장)을 분리해, 신규 커넥터는 고유 로직만 구현하면 되는 구조로 설계.
첫 커넥터로 쇼핑몰 통합관리 솔루션(사방넷) 연동 모듈 개발.

## 담당업무 ↔ 코드

### 1) Core / 커넥터 2계층 구조 → [`1.CoreConnectorLayers/`](1.CoreConnectorLayers/)
> 연동처가 공통으로 필요로 하는 관심사를 Core로 분리하고 고유 로직만 커넥터에 배치. 신규 연동처 추가가 기존 연동에 영향을 주지 않도록 격리

| 파일 | 내용 |
|---|---|
| [`Core/ConnectorBase.cs`](1.CoreConnectorLayers/Core/ConnectorBase.cs) | Core 계층 — 설정 스냅샷·인증·로깅·예외→판정 변환·전송 보장을 표준 흐름으로 고정 |
| [`Connectors/Sabangnet/SabangnetOrderConnector.cs`](1.CoreConnectorLayers/Connectors/Sabangnet/SabangnetOrderConnector.cs) | 커넥터 계층 — 사방넷 주문 업무 흐름만 구현 |
| [`Connectors/Sabangnet/SabangnetClient.cs`](1.CoreConnectorLayers/Connectors/Sabangnet/SabangnetClient.cs) | REST/JSON 클라이언트, 명세에 없는 예외 동작(200 + FAIL body) 검증 |

### 2) 업무 규칙의 설정화 → [`2.RuleConfiguration/`](2.RuleConfiguration/)
> 브랜드·거래처마다 달라지는 분기를 코드가 아닌 환경설정으로 외부화. 신규 브랜드·거래처 대응이 배포 없이 설정 등록만으로 완료

| 파일 | 내용 |
|---|---|
| [`RuleConfigSchema.cs`](2.RuleConfiguration/RuleConfigSchema.cs) | 단일값·목록·코드 매핑표 3종을 하나의 스키마로 수용, 스코프 특이도 |
| [`RuleConfigProvider.cs`](2.RuleConfiguration/RuleConfigProvider.cs) | 브랜드·센터·서비스 단위 스냅샷 + 기본값 병합 규칙 (EF Core) |

### 3) 운영자 중심의 결과 모델 → [`3.ResultModel/`](3.ResultModel/)
> 실행 결과를 건 단위로 표현하고 단계별 판정을 누적하는 응답 모델로 재설계. 실패 알림만으로 대상 주문과 실패 지점을 즉시 특정 가능

| 파일 | 내용 |
|---|---|
| [`ItemResult.cs`](3.ResultModel/ItemResult.cs) | 건 단위 결과 + 단계별 판정 누적 (Map→Validate→WmsCommit→ExternalCommit) |
| [`FailureNotificationFormatter.cs`](3.ResultModel/FailureNotificationFormatter.cs) | 건수가 아닌 "주문 + 실패 지점 + 상대 코드" 알림 |

### 4) 시스템 간 정합성 보장 → [`4.Consistency/`](4.Consistency/)
> 하나의 트랜잭션으로 묶을 수 없는 자사 WMS와 외부 시스템 간 처리 순서를 WMS 우선으로 고정해 데이터 불일치 방지

| 파일 | 내용 |
|---|---|
| [`WmsFirstOrderingPolicy.cs`](4.Consistency/WmsFirstOrderingPolicy.cs) | WMS 우선 순서 강제 — 왜 그 순서여야 하는지 주석에 |
| [`Outbox.cs`](4.Consistency/Outbox.cs) | 이력·알림·재전송 의도를 한 트랜잭션으로 저장, 디스패처가 배출 |

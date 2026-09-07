# [프로젝트 3] 이비즈웨이 ERP 연동 인터페이스

| | |
|---|---|
| 기간 | 2026.06 ~ 2026.08 |
| 규모 | 2명 (연동 인터페이스 서버 · WMS 연동 모듈 담당) |
| 기술 스택 | C#, ASP.NET(ASMX/SOAP), WinForms, Infragistics, MS SQL Server, ASP.NET Core, React |

**개요** — 브랜드사 ERP(이비즈웨이)와 자사 WMS 간 마스터·입출고·온라인 주문·반품 데이터를 주고받는 양방향 연동 인터페이스 신규 구축.
총 24종 인터페이스 설계, 5개 브랜드 운영 전환 완료.

## 담당업무 ↔ 코드

### 1) 반복 관심사의 공통화 → [`1.CoreAbstraction/`](1.CoreAbstraction/)
> 조회 9종·수신 15종 총 24개 오퍼레이션에서 반복되는 유효성 검증·데이터 복원·예외 변환 로직을 상위 Core 클래스로 추상화해 신규 인터페이스 추가 비용 최소화

| 파일 | 내용 |
|---|---|
| [`InterfaceOperationBase.cs`](1.CoreAbstraction/InterfaceOperationBase.cs) | 검증 → 복원 → 처리 → 예외 변환을 한 번만 구현한 상위 Core |
| [`Operations/ReceiveOnlineOrderOperation.cs`](1.CoreAbstraction/Operations/ReceiveOnlineOrderOperation.cs) | 수신·조회 오퍼레이션 예시 — 고유 규칙과 처리만 |
| [`ErpInterfaceService.asmx.cs`](1.CoreAbstraction/ErpInterfaceService.asmx.cs) | SOAP 엔드포인트 24종 (WebMethod = 위임 한 줄) |

### 2) 상대 시스템에 의존하지 않는 검증 환경 → [`2.Verification/`](2.Verification/)
> 스키마 기반 더미데이터 생성기와 검증기/전송기 분리로 상대 시스템 준비 이전 단계에서도 전 시나리오 사전 검증, 일정 지연 없는 개발 진행

| 파일 | 내용 |
|---|---|
| [`SchemaDummyDataGenerator.cs`](2.Verification/SchemaDummyDataGenerator.cs) | DTO 스키마 리플렉션 + **실제 마스터 코드 샘플링** 으로 유효 전문 생성 |
| [`ValidatorAndTransmitter.cs`](2.Verification/ValidatorAndTransmitter.cs) | 검증기(전송 없음)와 전송기(검증 통과분만) 분리, 시나리오 매트릭스 |
| [`AdminDummyDataController.cs`](2.Verification/AdminDummyDataController.cs) | 관리자 페이지 API — 생성 → 검증 → 전송 |

### 3) WMS 연동과 자동화 연계 → [`3.WmsIntegration/`](3.WmsIntegration/)
> 브랜드별 배송 정책을 반영한 WMS 연동 화면 개발, 반품 회수 지시를 통합 스케줄러 도메인으로 등록해 수동 운영 자동화

| 파일 | 내용 |
|---|---|
| [`BrandDeliveryPolicyForm.cs`](3.WmsIntegration/BrandDeliveryPolicyForm.cs) | WinForms + Infragistics UltraGrid 브랜드별 배송 정책 화면 |
| [`ReturnPickupAutomation.sql`](3.WmsIntegration/ReturnPickupAutomation.sql) | 반품 회수 지시를 스케줄러 도메인으로 등록 — 배포 없이 행 두 줄 |

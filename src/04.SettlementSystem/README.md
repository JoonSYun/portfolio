# [프로젝트 4] 물류센터 정산 시스템

| | |
|---|---|
| 기간 | 2025.04 ~ 2025.11 |
| 규모 | 2명 |
| 기술 스택 | C#, WinForms, Infragistics, MS SQL Server |

**개요** — 엑셀 수작업으로 처리하던 물류센터 정산 업무 시스템화.
근태 입력부터 매출·비용 산정, 실행원가명세서·청구내역서 발행까지 end-to-end 정산 파이프라인 구축.

## 담당업무 ↔ 코드

### 1) 복잡한 정산 규칙의 데이터 모델화 → [`1.DataModel/`](1.DataModel/)
> 매출·인건비·운반비·일반경비 등 40개 이상 비용 항목을 수용하는 정산 데이터 모델 설계, 화면별 전용 프로시저로 대량 조회 성능 확보

| 파일 | 내용 |
|---|---|
| [`settlement_schema.sql`](1.DataModel/settlement_schema.sql) | 항목을 컬럼이 아닌 행으로 — 분류/항목 마스터 + (기간, 항목) 실적, 근태·계약단가 |
| [`usp_SettlementSummary_ByScreen.sql`](1.DataModel/usp_SettlementSummary_ByScreen.sql) | 화면 전용 프로시저: 임시 테이블 분산 조회 → 인덱스 → 집계 조인 |

### 2) 수작업의 자동화 → [`2.Automation/`](2.Automation/)
> 채용형태별 근무시간 집계, 단가 기반 인건비 자동 산정, 계약단가와 WMS 물동량을 연동한 매출 자동 계산 구현으로 수작업 정산을 시스템 산출로 대체

| 파일 | 내용 |
|---|---|
| [`usp_CalculateSettlement.sql`](2.Automation/usp_CalculateSettlement.sql) | 근태 집계 → 인건비 → 물동량 매출 → 공통비 안분, 한 트랜잭션. 수동 입력은 보존 |
| [`SettlementCalculator.cs`](2.Automation/SettlementCalculator.cs) | 계산 전 점검(근태 누락일·단가 미등록), 호출, 명세서 로드 |

### 3) 현업 생산성 도구 → [`3.ProductivityTools/`](3.ProductivityTools/)
> 엑셀 셀 범위를 그대로 붙여넣는 복사/붙여넣기 엔진과 Import/Export 등 공통 컴포넌트로 20여 개 입력 화면의 개발·운영 생산성 향상

| 파일 | 내용 |
|---|---|
| [`ExcelPasteEngine.cs`](3.ProductivityTools/ExcelPasteEngine.cs) | 클립보드 셀 범위 → UltraGrid, 타입 변환, 실패 셀 표시 후 계속 |
| [`GridImportExport.cs`](3.ProductivityTools/GridImportExport.cs) | 헤더명 매칭 Import / 보이는 그대로 Export (템플릿 = 내보낸 파일) |

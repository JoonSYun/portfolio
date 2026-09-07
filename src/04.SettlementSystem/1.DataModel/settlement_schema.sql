-- [담당업무 1] 복잡한 정산 규칙의 데이터 모델화 (MS SQL Server)
--
-- 엑셀 시트마다 제각각이던 정산 항목을 하나의 모델로 수용한다.
-- 핵심은 "비용 항목을 컬럼으로 늘리지 않는다" — 매출·인건비·운반비·일반경비 등 40개 이상 항목은
-- CostCategory / CostItem 마스터로 정의하고, 실적은 (센터, 월, 항목) 행으로 쌓는다.
-- 항목이 추가돼도 스키마는 바뀌지 않는다.

CREATE SCHEMA stl;
GO

-- 비용 분류 (대분류) : 매출 / 인건비 / 운반비 / 일반경비 / 설비 …
CREATE TABLE stl.CostCategory (
    CategoryCode  VARCHAR(20)  NOT NULL PRIMARY KEY,
    CategoryName  NVARCHAR(50) NOT NULL,
    Sign          SMALLINT     NOT NULL,            -- +1 매출, -1 비용
    SortOrder     INT          NOT NULL
);

-- 비용 항목 (40+) : 정규직 인건비, 일용직 인건비, 지게차 임차, 소모품, 간선 운송비, 택배비 …
CREATE TABLE stl.CostItem (
    ItemCode      VARCHAR(30)  NOT NULL PRIMARY KEY,
    CategoryCode  VARCHAR(20)  NOT NULL REFERENCES stl.CostCategory(CategoryCode),
    ItemName      NVARCHAR(80) NOT NULL,
    CalcMethod    VARCHAR(20)  NOT NULL,            -- MANUAL | LABOR_AUTO | VOLUME_RATE | ALLOCATED
    Unit          NVARCHAR(10) NULL,                -- 원, 시간, 건, CBM
    IsBillable    BIT          NOT NULL DEFAULT 1   -- 청구내역서 포함 여부
);

-- 월별 센터 정산 헤더
CREATE TABLE stl.SettlementPeriod (
    PeriodId      INT IDENTITY PRIMARY KEY,
    CenterCode    VARCHAR(20)  NOT NULL,
    ClientCode    VARCHAR(20)  NOT NULL,            -- 화주
    YearMonth     CHAR(6)      NOT NULL,            -- 202507
    Status        VARCHAR(10)  NOT NULL DEFAULT 'OPEN',  -- OPEN → CALCULATED → CONFIRMED → BILLED
    ConfirmedAt   DATETIME2    NULL,
    CONSTRAINT UQ_Period UNIQUE (CenterCode, ClientCode, YearMonth)
);

-- 정산 실적 : (기간, 항목) 당 한 행. 40개 항목 = 40행. 컬럼이 아니다.
CREATE TABLE stl.SettlementLine (
    PeriodId      INT           NOT NULL REFERENCES stl.SettlementPeriod(PeriodId),
    ItemCode      VARCHAR(30)   NOT NULL REFERENCES stl.CostItem(ItemCode),
    Quantity      DECIMAL(18,3) NULL,               -- 시간·건·CBM
    UnitPrice     DECIMAL(18,2) NULL,               -- 계약 단가
    Amount        DECIMAL(18,0) NOT NULL,
    Source        VARCHAR(20)   NOT NULL,           -- AUTO | MANUAL | IMPORT
    Note          NVARCHAR(200) NULL,
    CONSTRAINT PK_SettlementLine PRIMARY KEY (PeriodId, ItemCode)
);

-- 근태 원장 : 인건비 자동 산정의 입력
CREATE TABLE stl.Attendance (
    AttendanceId  BIGINT IDENTITY PRIMARY KEY,
    CenterCode    VARCHAR(20) NOT NULL,
    WorkDate      DATE        NOT NULL,
    WorkerId      VARCHAR(20) NOT NULL,
    EmployType    VARCHAR(10) NOT NULL,             -- REGULAR | DAILY | AGENCY  (채용형태)
    StartTime     TIME        NOT NULL,
    EndTime       TIME        NOT NULL,
    BreakMinutes  INT         NOT NULL DEFAULT 60,
    INDEX IX_Attendance_Center_Date (CenterCode, WorkDate)
);

-- 계약 단가 : 채용형태별 시급 / 물동량 단위 매출 단가
CREATE TABLE stl.ContractRate (
    ClientCode    VARCHAR(20)   NOT NULL,
    CenterCode    VARCHAR(20)   NOT NULL,
    RateKey       VARCHAR(30)   NOT NULL,           -- LABOR_REGULAR_HOUR | LABOR_DAILY_HOUR | INBOUND_PER_CBM | OUTBOUND_PER_ORDER …
    Rate          DECIMAL(18,2) NOT NULL,
    ValidFrom     DATE          NOT NULL,
    ValidTo       DATE          NULL,
    CONSTRAINT PK_ContractRate PRIMARY KEY (ClientCode, CenterCode, RateKey, ValidFrom)
);

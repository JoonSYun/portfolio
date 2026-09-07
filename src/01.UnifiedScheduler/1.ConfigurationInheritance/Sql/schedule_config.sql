-- [담당업무 1] 스케줄 설정의 단일 진실 소스 (MS SQL Server)
-- 코드에 흩어져 있던 스케줄을 두 테이블로 옮겼다.
--   sch.DomainSchedule        : 도메인 기본값 (변하지 않는 실행 규칙)
--   sch.BrandScheduleOverride : 브랜드별 오버라이드 (변하는 것만, NULL = 상속)

CREATE SCHEMA sch;
GO

CREATE TABLE sch.DomainSchedule (
    DomainCode      VARCHAR(50)   NOT NULL PRIMARY KEY,
    GroupCode       VARCHAR(50)   NOT NULL,            -- 운영 제어 단위(그룹)
    CronExpression  VARCHAR(100)  NOT NULL,
    QueueName       VARCHAR(50)   NOT NULL,            -- 큐 = 워커 서버 (장애 격리 경계)
    TimeoutSeconds  INT           NOT NULL DEFAULT 300,
    MaxRetry        INT           NOT NULL DEFAULT 3,
    Enabled         BIT           NOT NULL DEFAULT 1,
    ParametersJson  NVARCHAR(MAX) NULL,                -- 도메인 기본 파라미터
    BrandCodesCsv   VARCHAR(1000) NULL,                -- fan-out 대상 브랜드
    UpdatedAt       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy       VARCHAR(50)   NULL
);

CREATE TABLE sch.BrandScheduleOverride (
    DomainCode             VARCHAR(50)   NOT NULL,
    BrandCode              VARCHAR(50)   NOT NULL,
    CronExpression         VARCHAR(100)  NULL,         -- NULL = 도메인 값 상속
    TimeoutSeconds         INT           NULL,
    MaxRetry               INT           NULL,
    Enabled                BIT           NULL,
    ParameterOverridesJson NVARCHAR(MAX) NULL,         -- 덮어쓸 키만
    UpdatedAt              DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy              VARCHAR(50)   NULL,
    CONSTRAINT PK_BrandScheduleOverride PRIMARY KEY (DomainCode, BrandCode),
    CONSTRAINT FK_BrandScheduleOverride_Domain FOREIGN KEY (DomainCode) REFERENCES sch.DomainSchedule(DomainCode)
);

-- 신규 브랜드 대응 = 행 하나 추가. 배포 없음.
-- INSERT sch.BrandScheduleOverride (DomainCode, BrandCode, ParameterOverridesJson)
-- VALUES ('RETURN_PICKUP', 'BRAND_X', N'{"centerCode":"C03"}');

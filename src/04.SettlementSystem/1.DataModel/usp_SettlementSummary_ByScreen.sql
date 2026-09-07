-- [담당업무 1] 화면별 전용 프로시저로 대량 조회 성능 확보
--
-- 실행원가명세서 화면 하나가 한 달치 근태(수만 행) × 항목(40+) × 센터를 조인해야 한다.
-- 범용 뷰로는 매번 전체 조인이 돌아 수십 초. 화면마다 "필요한 것만, 필요한 순서로" 만드는 전용 프로시저로 바꿨다.
-- 패턴: 대량 원천을 임시 테이블로 먼저 분산 조회 → 임시 테이블에 인덱스 → 최종 집계 조인.

CREATE OR ALTER PROCEDURE stl.usp_ExecutionCostStatement
    @CenterCode  VARCHAR(20),
    @ClientCode  VARCHAR(20),
    @YearMonth   CHAR(6)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From DATE = DATEFROMPARTS(LEFT(@YearMonth,4), RIGHT(@YearMonth,2), 1);
    DECLARE @To   DATE = EOMONTH(@From);

    -- 1) 근태 → 채용형태별 시간 집계를 임시 테이블로 (원천 수만 행을 여기서 수백 행으로 줄인다)
    SELECT  EmployType,
            SUM(DATEDIFF(MINUTE, StartTime, EndTime) - BreakMinutes) / 60.0 AS WorkHours,
            COUNT(DISTINCT WorkerId) AS HeadCount
    INTO    #Labor
    FROM    stl.Attendance
    WHERE   CenterCode = @CenterCode AND WorkDate BETWEEN @From AND @To
    GROUP BY EmployType;
    CREATE CLUSTERED INDEX IX ON #Labor(EmployType);

    -- 2) 유효 계약 단가만 임시 테이블로
    SELECT  RateKey, Rate
    INTO    #Rate
    FROM    stl.ContractRate
    WHERE   ClientCode = @ClientCode AND CenterCode = @CenterCode
      AND   ValidFrom <= @To AND (ValidTo IS NULL OR ValidTo >= @From);
    CREATE CLUSTERED INDEX IX ON #Rate(RateKey);

    -- 3) 확정된 정산 라인 (수동/자동 모두)
    SELECT  l.ItemCode, l.Quantity, l.UnitPrice, l.Amount, l.Source
    INTO    #Line
    FROM    stl.SettlementLine l
    JOIN    stl.SettlementPeriod p ON p.PeriodId = l.PeriodId
    WHERE   p.CenterCode = @CenterCode AND p.ClientCode = @ClientCode AND p.YearMonth = @YearMonth;
    CREATE CLUSTERED INDEX IX ON #Line(ItemCode);

    -- 4) 최종: 분류 → 항목 순서로, 항목이 없어도 0 으로 행을 만든다 (명세서는 항목이 고정 출력)
    SELECT  c.CategoryCode, c.CategoryName, c.Sign,
            i.ItemCode, i.ItemName, i.Unit, i.CalcMethod,
            ISNULL(ln.Quantity, 0)  AS Quantity,
            ISNULL(ln.UnitPrice, 0) AS UnitPrice,
            ISNULL(ln.Amount, 0) * c.Sign AS SignedAmount,
            ISNULL(ln.Source, '-') AS Source
    FROM    stl.CostCategory c
    JOIN    stl.CostItem i ON i.CategoryCode = c.CategoryCode
    LEFT JOIN #Line ln ON ln.ItemCode = i.ItemCode
    WHERE   i.IsBillable = 1
    ORDER BY c.SortOrder, i.ItemCode;

    -- 5) 보조 결과셋: 인건비 근거 (명세서 하단 "근태 집계" 표)
    SELECT  l.EmployType, l.WorkHours, l.HeadCount, r.Rate AS HourlyRate, l.WorkHours * r.Rate AS LaborCost
    FROM    #Labor l
    LEFT JOIN #Rate r ON r.RateKey = 'LABOR_' + l.EmployType + '_HOUR';
END
GO

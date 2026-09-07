-- [담당업무 2] 수작업의 자동화 — 정산 계산 프로시저
--
-- 엑셀에서 손으로 하던 세 가지를 시스템 산출로 대체한다:
--   ① 채용형태별 근무시간 집계          (Attendance → 시간)
--   ② 단가 기반 인건비 자동 산정         (시간 × ContractRate)
--   ③ 계약단가 × WMS 물동량 매출 자동 계산 (WMS 수불 → 건/CBM × 단가)
-- 한 트랜잭션으로 SettlementLine 을 갱신한다. 수동 입력(Source='MANUAL') 은 덮어쓰지 않는다.

CREATE OR ALTER PROCEDURE stl.usp_CalculateSettlement
    @PeriodId INT,
    @OperatorId VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @CenterCode VARCHAR(20), @ClientCode VARCHAR(20), @YearMonth CHAR(6), @Status VARCHAR(10);
    SELECT @CenterCode = CenterCode, @ClientCode = ClientCode, @YearMonth = YearMonth, @Status = Status
    FROM   stl.SettlementPeriod WHERE PeriodId = @PeriodId;

    IF @Status IN ('CONFIRMED', 'BILLED')
        THROW 50001, N'확정된 정산은 재계산할 수 없습니다.', 1;

    DECLARE @From DATE = DATEFROMPARTS(LEFT(@YearMonth,4), RIGHT(@YearMonth,2), 1);
    DECLARE @To   DATE = EOMONTH(@From);

    BEGIN TRAN;

    -- ① 근무시간 집계 (채용형태별)
    ;WITH Labor AS (
        SELECT EmployType,
               SUM(DATEDIFF(MINUTE, StartTime, EndTime) - BreakMinutes) / 60.0 AS Hours
        FROM   stl.Attendance
        WHERE  CenterCode = @CenterCode AND WorkDate BETWEEN @From AND @To
        GROUP BY EmployType
    ),
    -- ② 인건비 = 시간 × 채용형태별 시급
    LaborCost AS (
        SELECT 'LABOR_' + l.EmployType AS ItemCode, l.Hours AS Quantity, r.Rate AS UnitPrice, ROUND(l.Hours * r.Rate, 0) AS Amount
        FROM   Labor l
        JOIN   stl.ContractRate r ON r.ClientCode = @ClientCode AND r.CenterCode = @CenterCode
                                 AND r.RateKey = 'LABOR_' + l.EmployType + '_HOUR'
                                 AND r.ValidFrom <= @To AND (r.ValidTo IS NULL OR r.ValidTo >= @From)
    ),
    -- ③ 매출 = WMS 물동량 × 계약 단가 (입고 CBM, 출고 주문 건, 반품 건 …)
    Volume AS (
        SELECT 'REV_INBOUND'  AS ItemCode, 'INBOUND_PER_CBM'    AS RateKey, SUM(Cbm)      AS Quantity FROM wms.InboundClosed  WHERE CenterCode=@CenterCode AND ClientCode=@ClientCode AND ClosedDate BETWEEN @From AND @To
        UNION ALL
        SELECT 'REV_OUTBOUND', 'OUTBOUND_PER_ORDER', COUNT(*)  FROM wms.OutboundClosed WHERE CenterCode=@CenterCode AND ClientCode=@ClientCode AND ClosedDate BETWEEN @From AND @To
        UNION ALL
        SELECT 'REV_RETURN',   'RETURN_PER_ORDER',   COUNT(*)  FROM wms.ReturnClosed   WHERE CenterCode=@CenterCode AND ClientCode=@ClientCode AND ClosedDate BETWEEN @From AND @To
    ),
    Revenue AS (
        SELECT v.ItemCode, v.Quantity, r.Rate AS UnitPrice, ROUND(v.Quantity * r.Rate, 0) AS Amount
        FROM   Volume v
        JOIN   stl.ContractRate r ON r.ClientCode = @ClientCode AND r.CenterCode = @CenterCode AND r.RateKey = v.RateKey
                                 AND r.ValidFrom <= @To AND (r.ValidTo IS NULL OR r.ValidTo >= @From)
    ),
    Calculated AS (SELECT * FROM LaborCost UNION ALL SELECT * FROM Revenue)

    -- 자동 산출 라인 upsert. MANUAL 은 건드리지 않는다.
    MERGE stl.SettlementLine AS t
    USING Calculated AS s ON t.PeriodId = @PeriodId AND t.ItemCode = s.ItemCode
    WHEN MATCHED AND t.Source <> 'MANUAL' THEN
        UPDATE SET Quantity = s.Quantity, UnitPrice = s.UnitPrice, Amount = s.Amount, Source = 'AUTO'
    WHEN NOT MATCHED THEN
        INSERT (PeriodId, ItemCode, Quantity, UnitPrice, Amount, Source)
        VALUES (@PeriodId, s.ItemCode, s.Quantity, s.UnitPrice, s.Amount, 'AUTO');

    -- 월별 운영비용 자동 분배: 센터 공통비(임차료 등)를 화주별 출고 비중으로 안분
    EXEC stl.usp_AllocateSharedCost @PeriodId;

    UPDATE stl.SettlementPeriod SET Status = 'CALCULATED' WHERE PeriodId = @PeriodId;

    INSERT stl.CalcLog (PeriodId, OperatorId, CalculatedAt) VALUES (@PeriodId, @OperatorId, SYSUTCDATETIME());

    COMMIT;
END
GO

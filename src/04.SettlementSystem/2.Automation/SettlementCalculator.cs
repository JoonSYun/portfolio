using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;

namespace Portfolio.Settlement.Automation
{
    /// <summary>
    /// [담당업무 2] 수작업의 자동화 — 클라이언트 측 계산 서비스.
    /// 무거운 집계는 프로시저(usp_CalculateSettlement)에 있고, 화면은 이 서비스로 호출·검증·미리보기를 한다.
    /// 계산 전 "근태가 빠진 날" 을 먼저 잡아내는 것이 실무에서 가장 많이 걸린 오류였다.
    /// </summary>
    public sealed class SettlementCalculator
    {
        private readonly IDbConnection _db;
        public SettlementCalculator(IDbConnection db) { _db = db; }

        /// <summary>계산 전 점검 — 근태 누락일, 단가 미등록 항목을 미리 알려준다.</summary>
        public PreflightResult Preflight(int periodId)
        {
            var p = _db.QuerySingle<(string Center, string Client, string YearMonth)>(
                "SELECT CenterCode, ClientCode, YearMonth FROM stl.SettlementPeriod WHERE PeriodId=@periodId", new { periodId });

            var from = new DateTime(int.Parse(p.YearMonth.Substring(0, 4)), int.Parse(p.YearMonth.Substring(4, 2)), 1);
            var days = Enumerable.Range(0, DateTime.DaysInMonth(from.Year, from.Month)).Select(i => from.AddDays(i));

            var attended = _db.Query<DateTime>(
                "SELECT DISTINCT WorkDate FROM stl.Attendance WHERE CenterCode=@Center AND WorkDate BETWEEN @from AND @to",
                new { p.Center, from, to = from.AddMonths(1).AddDays(-1) }).ToHashSet();

            var missingDays = days.Where(d => d.DayOfWeek != DayOfWeek.Sunday && !attended.Contains(d)).ToList();

            var missingRates = _db.Query<string>(@"
                SELECT k.RateKey FROM (VALUES ('LABOR_REGULAR_HOUR'),('LABOR_DAILY_HOUR'),('INBOUND_PER_CBM'),('OUTBOUND_PER_ORDER')) k(RateKey)
                WHERE NOT EXISTS (SELECT 1 FROM stl.ContractRate r WHERE r.ClientCode=@Client AND r.CenterCode=@Center AND r.RateKey=k.RateKey
                                  AND r.ValidFrom <= @to AND (r.ValidTo IS NULL OR r.ValidTo >= @from))",
                new { p.Client, p.Center, from, to = from.AddMonths(1).AddDays(-1) }).ToList();

            return new PreflightResult(missingDays, missingRates);
        }

        /// <summary>실제 계산 — 프로시저 호출. 확정 상태면 프로시저가 THROW 한다.</summary>
        public void Calculate(int periodId, string operatorId) =>
            _db.Execute("stl.usp_CalculateSettlement", new { PeriodId = periodId, OperatorId = operatorId }, commandType: CommandType.StoredProcedure);

        /// <summary>실행원가명세서 데이터 — 화면 전용 프로시저, 결과셋 2개 (명세 + 인건비 근거).</summary>
        public CostStatement LoadStatement(string center, string client, string yearMonth)
        {
            using (var multi = _db.QueryMultiple("stl.usp_ExecutionCostStatement",
                new { CenterCode = center, ClientCode = client, YearMonth = yearMonth }, commandType: CommandType.StoredProcedure))
            {
                return new CostStatement(multi.Read<StatementLine>().ToList(), multi.Read<LaborBasis>().ToList());
            }
        }
    }

    public sealed class PreflightResult
    {
        public IReadOnlyList<DateTime> MissingAttendanceDays { get; }
        public IReadOnlyList<string> MissingRateKeys { get; }
        public bool CanCalculate => MissingAttendanceDays.Count == 0 && MissingRateKeys.Count == 0;
        public PreflightResult(List<DateTime> days, List<string> rates) { MissingAttendanceDays = days; MissingRateKeys = rates; }
    }

    public sealed class StatementLine
    {
        public string CategoryCode { get; set; } public string CategoryName { get; set; } public short Sign { get; set; }
        public string ItemCode { get; set; } public string ItemName { get; set; } public string Unit { get; set; } public string CalcMethod { get; set; }
        public decimal Quantity { get; set; } public decimal UnitPrice { get; set; } public decimal SignedAmount { get; set; } public string Source { get; set; }
    }
    public sealed class LaborBasis { public string EmployType { get; set; } public decimal WorkHours { get; set; } public int HeadCount { get; set; } public decimal HourlyRate { get; set; } public decimal LaborCost { get; set; } }
    public sealed class CostStatement
    {
        public IReadOnlyList<StatementLine> Lines { get; }
        public IReadOnlyList<LaborBasis> Labor { get; }
        public decimal NetAmount => Lines.Sum(l => l.SignedAmount);
        public CostStatement(List<StatementLine> lines, List<LaborBasis> labor) { Lines = lines; Labor = labor; }
    }
}

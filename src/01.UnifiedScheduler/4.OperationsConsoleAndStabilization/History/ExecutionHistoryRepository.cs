using System.Data;
using Dapper;
namespace Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.History;

public interface IExecutionHistoryReader
{
    Task<IReadOnlyList<ExecutionHistoryRow>> SearchAsync(HistoryQuery q, CancellationToken ct);
}

public sealed record HistoryQuery(string? DomainCode, string? BrandCode, DateTime FromUtc, DateTime ToUtc, int Take = 200);
public sealed record ExecutionHistoryRow(Guid ExecutionId, string DomainCode, string BrandCode, string State,
    DateTime FiredAt, DateTime? FinishedAt, int ElapsedMs, int ProcessedCount, string? Message);

/// <summary>
/// [담당업무 4] 실행 이력 조회 (관리 콘솔용).
/// 이력의 상태 전이(ENQUEUED→RUNNING→SUCCESS/FAILED/DEPRECATED) 자체는
/// <c>2.ExecutionPipeline/Infrastructure/JobLogRepository.cs</c> 가 담당한다 — 이 클래스는 읽기 전용이다.
///
/// ⚠ 인시던트: 조회 데드락 (Incidents/QueryDeadlock_ParameterTypeMismatch.md)
///   VARCHAR 컬럼을 NVARCHAR 파라미터로 조회하면 암시적 변환 때문에 인덱스를 타지 못하고
///   테이블 스캔 → 쓰기 워커와 락 경합 → 데드락. Dapper 는 string 을 기본 NVARCHAR 로 보낸다.
///   해결: DbString(IsAnsi=true) 로 컬럼 타입과 정확히 일치시켜 인덱스 seek 을 회복.
/// </summary>
public sealed class ExecutionHistoryRepository : IExecutionHistoryReader
{
    private readonly IDbConnection _db;
    public ExecutionHistoryRepository(IDbConnection db) => _db = db;

    public async Task<IReadOnlyList<ExecutionHistoryRow>> SearchAsync(HistoryQuery q, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP (@Take) ExecutionId, DomainCode, BrandCode, State, FiredAt, FinishedAt, ElapsedMs, ProcessedCount, Message
            FROM   sch.ExecutionHistory WITH (READPAST)          -- 진행 중 행은 건너뛴다: 조회가 쓰기를 기다리지 않음
            WHERE  FiredAt BETWEEN @From AND @To
              AND (@Domain IS NULL OR DomainCode = @Domain)      -- IX_ExecutionHistory_Domain_FiredAt
              AND (@Brand  IS NULL OR BrandCode  = @Brand)
            ORDER BY FiredAt DESC;
            """;
        var rows = await _db.QueryAsync<ExecutionHistoryRow>(new CommandDefinition(sql, new
        {
            q.Take, From = q.FromUtc, To = q.ToUtc,
            Domain = q.DomainCode is null ? null : Ansi(q.DomainCode),   // ← 타입 일치가 핵심
            Brand = q.BrandCode is null ? null : Ansi(q.BrandCode)
        }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>VARCHAR 컬럼에는 반드시 ANSI 파라미터로 — NVARCHAR 로 보내면 인덱스를 잃는다.</summary>
    private static DbString Ansi(string value) => new() { Value = value, IsAnsi = true, Length = 50 };
}

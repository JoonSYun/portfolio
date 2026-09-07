using System.Data;
using System.Text.Json;
using Dapper;

namespace Portfolio.UnifiedScheduler.ConfigurationInheritance;

public interface IScheduleConfigRepository
{
    Task<DomainSchedule?> GetDomainAsync(string domainCode, CancellationToken ct);
    Task<IReadOnlyList<DomainSchedule>> GetEnabledDomainsAsync(CancellationToken ct);
    Task<IReadOnlyList<BrandScheduleOverride>> GetBrandOverridesAsync(string domainCode, CancellationToken ct);
}

/// <summary>
/// [담당업무 1] 스케줄 설정을 코드에서 DB로 분리 — "단일 진실 소스".
/// 코드에 흩어져 있던 하드코딩 스케줄을 두 테이블(도메인/브랜드)로 옮겨,
/// 관리 콘솔에서 행을 추가·수정하는 것만으로 서비스가 확장된다. (DDL: Sql/schedule_config.sql)
/// Dapper 로 얇게 읽는다 — 설정 조회는 파이프라인의 hot path 이므로 ORM 추적이 필요 없다.
/// </summary>
public sealed class ScheduleConfigRepository : IScheduleConfigRepository
{
    private readonly IDbConnection _db;
    public ScheduleConfigRepository(IDbConnection db) => _db = db;

    public async Task<DomainSchedule?> GetDomainAsync(string domainCode, CancellationToken ct)
    {
        const string sql = """
            SELECT DomainCode, GroupCode, CronExpression, QueueName,
                   TimeoutSeconds, MaxRetry, Enabled, ParametersJson, BrandCodesCsv
            FROM   sch.DomainSchedule
            WHERE  DomainCode = @DomainCode;
            """;
        var row = await _db.QuerySingleOrDefaultAsync<DomainRow>(new CommandDefinition(sql, new { DomainCode = domainCode }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<IReadOnlyList<DomainSchedule>> GetEnabledDomainsAsync(CancellationToken ct)
    {
        const string sql = "SELECT * FROM sch.DomainSchedule WHERE Enabled = 1;";
        var rows = await _db.QueryAsync<DomainRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<BrandScheduleOverride>> GetBrandOverridesAsync(string domainCode, CancellationToken ct)
    {
        const string sql = """
            SELECT DomainCode, BrandCode, CronExpression, TimeoutSeconds, MaxRetry, Enabled, ParameterOverridesJson
            FROM   sch.BrandScheduleOverride
            WHERE  DomainCode = @DomainCode;
            """;
        var rows = await _db.QueryAsync<OverrideRow>(new CommandDefinition(sql, new { DomainCode = domainCode }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    // ---- row → entity 매핑 (JSON 컬럼은 파라미터 딕셔너리로 복원) ----

    private sealed record DomainRow(string DomainCode, string GroupCode, string CronExpression, string QueueName,
        int TimeoutSeconds, int MaxRetry, bool Enabled, string? ParametersJson, string? BrandCodesCsv)
    {
        public DomainSchedule ToEntity() => new()
        {
            DomainCode = DomainCode, GroupCode = GroupCode, CronExpression = CronExpression, QueueName = QueueName,
            TimeoutSeconds = TimeoutSeconds, MaxRetry = MaxRetry, Enabled = Enabled,
            Parameters = ParametersJson is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(ParametersJson) ?? new(),
            BrandCodes = BrandCodesCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new()
        };
    }

    private sealed record OverrideRow(string DomainCode, string BrandCode, string? CronExpression,
        int? TimeoutSeconds, int? MaxRetry, bool? Enabled, string? ParameterOverridesJson)
    {
        public BrandScheduleOverride ToEntity() => new()
        {
            DomainCode = DomainCode, BrandCode = BrandCode, CronExpression = CronExpression,
            TimeoutSeconds = TimeoutSeconds, MaxRetry = MaxRetry, Enabled = Enabled,
            ParameterOverrides = ParameterOverridesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(ParameterOverridesJson)
        };
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Portfolio.IntegrationPlatform.RuleConfiguration;

public interface IRuleConfigProvider
{
    Task<RuleConfigSnapshot> SnapshotAsync(string connector, string brand, string center, string service, CancellationToken ct);
}

/// <summary>
/// [담당업무 2] 브랜드·센터·서비스 단위 스냅샷에 기본값 병합 규칙 적용.
///
/// 같은 Key 에 여러 스코프의 행이 있으면 특이도(Specificity)가 높은 것이 이긴다:
///   전역 기본값 < 브랜드 < 센터 < 브랜드+센터 < 서비스 < … < 브랜드+센터+서비스
/// 결과적으로 "이 브랜드의 이 센터에서 이 서비스로 돌릴 때"의 실효 설정 하나가 나온다.
/// 업무 분기가 코드 밖으로 완전히 나갔으므로, 신규 브랜드·거래처 대응 = 행 등록.
/// </summary>
public sealed class RuleConfigProvider : IRuleConfigProvider
{
    private readonly IntegrationDbContext _db;
    public RuleConfigProvider(IntegrationDbContext db) => _db = db;

    public async Task<RuleConfigSnapshot> SnapshotAsync(string connector, string brand, string center, string service, CancellationToken ct)
    {
        // 해당 스코프에 "적용 가능한" 행만 가져온다 (null 은 와일드카드)
        var rows = await _db.RuleConfigEntries.AsNoTracking()
            .Where(e => e.ConnectorCode == connector
                     && (e.BrandCode == null || e.BrandCode == brand)
                     && (e.CenterCode == null || e.CenterCode == center)
                     && (e.ServiceCode == null || e.ServiceCode == service))
            .ToListAsync(ct);

        // Key 별로 가장 특이한 행 하나 — 기본값 병합의 핵심 한 줄
        var effective = rows
            .GroupBy(e => e.Key)
            .Select(g => g.OrderByDescending(e => e.Specificity).First())
            .ToList();

        var values = new Dictionary<string, string>();
        var lists = new Dictionary<string, IReadOnlyList<string>>();
        var maps = new Dictionary<string, CodeMap>();

        foreach (var e in effective)
        {
            switch (e.Kind)
            {
                case RuleValueKind.SingleValue: values[e.Key] = e.ValueJson; break;
                case RuleValueKind.List: lists[e.Key] = JsonSerializer.Deserialize<List<string>>(e.ValueJson) ?? new(); break;
                case RuleValueKind.CodeMap: maps[e.Key] = new CodeMap(e.Key, JsonSerializer.Deserialize<Dictionary<string, string>>(e.ValueJson) ?? new()); break;
            }
        }
        return new RuleConfigSnapshot(values, lists, maps);
    }
}

public sealed class IntegrationDbContext : DbContext
{
    public IntegrationDbContext(DbContextOptions<IntegrationDbContext> o) : base(o) { }
    public DbSet<RuleConfigEntry> RuleConfigEntries => Set<RuleConfigEntry>();
    public DbSet<Consistency.OutboxMessage> Outbox => Set<Consistency.OutboxMessage>();
    public DbSet<ResultModel.IntegrationRunRecord> Runs => Set<ResultModel.IntegrationRunRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RuleConfigEntry>(e =>
        {
            e.ToTable("ConfigEntry", "rule");
            e.HasIndex(x => new { x.ConnectorCode, x.Key, x.BrandCode, x.CenterCode, x.ServiceCode }).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>();
        });
        b.Entity<Consistency.OutboxMessage>(e => { e.ToTable("Outbox", "integ"); e.HasIndex(x => x.PublishedAt); });
        b.Entity<ResultModel.IntegrationRunRecord>(e => e.ToTable("Run", "integ"));
    }
}

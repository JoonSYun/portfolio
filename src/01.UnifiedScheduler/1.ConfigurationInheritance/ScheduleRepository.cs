using Microsoft.EntityFrameworkCore;
using Portfolio.UnifiedScheduler.Data;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// SCH_DOMAIN / SCH_DOMAIN_SCHEDULE / SCH_DOMAIN_BRAND_OVERRIDE 조회 + Sync 체크포인트.
/// IDbContextFactory 로 메서드마다 컨텍스트 생성 (short-lived).
///
/// Domain ↔ Group 활성 시멘틱 (V008 이후, AND + 0개 vacuously-true):
///   도메인 활성 = SCH_DOMAIN.USE_YN='Y' AND
///                 매핑된 그룹 중 USE_YN='N' 인 것이 하나도 없음
///   (그룹 0개면 그룹 필터 자동 통과)
/// → SQL 로는 "NOT EXISTS (... map JOIN group WHERE g.USE_YN='N')" 한 줄로 표현.
/// </summary>
public class ScheduleRepository
{
    private readonly IDbContextFactory<SchedulerHubDbContext> _factory;

    public ScheduleRepository(IDbContextFactory<SchedulerHubDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 활성 도메인 전체 (cron/큐/tz 포함 full row). Bootstrapper 기동 등록과
    /// <see cref="Portfolio.UnifiedScheduler.Jobs.ScheduleSyncJob"/> 2단계 reconcile 의 공용 "활성" 단일 정의.
    /// 활성 = SCH_DOMAIN.USE_YN='Y' / 그룹 활성 / SCH_DOMAIN_SCHEDULE.USE_YN='Y' / VALID_FROM·VALID_TO 범위 내.
    /// reconcile 은 (Hangfire 등록 셋) 과 (이 셋) 의 양방향 차집합으로 dead 제거 + missing 재등록을 모두 처리하므로
    /// 활성 조건이 바뀌면 이 메서드만 고치면 된다.
    /// </summary>
    public IReadOnlyList<DomainScheduleRow> GetActiveDomainSchedules()
    {
        using var ctx = _factory.CreateDbContext();
        var today = DateOnly.FromDateTime(DateTime.Today);

        return ctx.Domains
            .AsNoTracking()
            .Where(d => d.UseYn == "Y")
            .Where(d => !ctx.DomainGroupMaps
                            .Where(m => m.DomainCode == d.DomainCode)
                            .Join(ctx.DomainGroups, m => m.GroupCode, g => g.GroupCode, (m, g) => g)
                            .Any(g => g.UseYn == "N"))
            .Join(ctx.Schedules.AsNoTracking(),
                  d => d.DomainCode,
                  s => s.DomainCode,
                  (d, s) => new { d, s })
            .Where(x => x.s.UseYn == "Y")
            .Where(x => x.s.ValidFrom == null || x.s.ValidFrom <= today)
            .Where(x => x.s.ValidTo   == null || x.s.ValidTo   >= today)
            .Select(x => new DomainScheduleRow
            {
                DomainCode   = x.d.DomainCode,
                DefaultQueue = x.d.DefaultQueue,
                JobType      = x.d.JobType,
                CronExpr     = x.s.CronExpr,
                TimezoneId   = x.s.TimezoneId,
                UpdDt        = x.s.UpdDt
            })
            .ToList();
    }

    /// <summary>Sync Job — UPD_DT > SYNCED_UPD_DT 인 row만.</summary>
    public IReadOnlyList<DomainScheduleRow> GetChangedSchedules()
    {
        using var ctx = _factory.CreateDbContext();

        return ctx.Domains
            .AsNoTracking()
            .Where(d => d.UseYn == "Y")
            .Where(d => !ctx.DomainGroupMaps
                            .Where(m => m.DomainCode == d.DomainCode)
                            .Join(ctx.DomainGroups, m => m.GroupCode, g => g.GroupCode, (m, g) => g)
                            .Any(g => g.UseYn == "N"))
            .Join(ctx.Schedules.AsNoTracking(),
                  d => d.DomainCode,
                  s => s.DomainCode,
                  (d, s) => new { d, s })
            .Where(x => x.s.UseYn == "Y")
            .Where(x => x.s.SyncedUpdDt == null || x.s.SyncedUpdDt < x.s.UpdDt)
            .Select(x => new DomainScheduleRow
            {
                DomainCode   = x.d.DomainCode,
                DefaultQueue = x.d.DefaultQueue,
                JobType      = x.d.JobType,
                CronExpr     = x.s.CronExpr,
                TimezoneId   = x.s.TimezoneId,
                UpdDt        = x.s.UpdDt
            })
            .ToList();
    }

    /// <summary>체크포인트 기록 — 업데이트를 ExecuteUpdate 로 직접 (변경 추적 없이).</summary>
    public void MarkSynced(string domainCode, DateTime updDt)
    {
        using var ctx = _factory.CreateDbContext();

        ctx.Schedules
            .Where(s => s.DomainCode == domainCode)
            .ExecuteUpdate(setters => setters.SetProperty(s => s.SyncedUpdDt, updDt));
    }

    /// <summary>
    /// Admin "Run now" / "Run domain now" 사전 검증 — SCH_DOMAIN.USE_YN 조회.
    /// 도메인 row 부재면 null, 존재하면 "Y" 또는 "N" 반환.
    /// 호출자(Dispatcher) 가 NotFound("Domain") 와 BusinessRule(비활성) 을 구분해 던지기 위한 헬퍼.
    /// </summary>
    /// <returns>도메인 부재 시 null. 존재 시 SCH_DOMAIN.USE_YN 값 ("Y" / "N").</returns>
    public string? GetDomainUseYn(string domainCode)
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.Domains.AsNoTracking()
            .Where(d => d.DomainCode == domainCode)
            .Select(d => d.UseYn)
            .FirstOrDefault();
    }

    /// <summary>
    /// Admin "Run now" 전용 — 단일 (DomainCode, BrandCode) 페어를 조회.
    /// 그룹 활성 / 스케줄 RUN_AUTO_YN / 브랜드·오버라이드 USE_YN / 날짜 필터 모두 우회 — 관리자가
    /// 의식적으로 누른 디버깅 액션. 단, SCH_DOMAIN.USE_YN='N' 가드는 Dispatcher.EnqueueOne 이
    /// 별도로 적용하므로 본 조회는 도메인 USE_YN 도 무시.
    /// row 자체가 없거나 도메인이 삭제된 경우만 null.
    /// </summary>
    public TargetRow? GetTargetForManualRun(string domainCode, string brandCode)
    {
        using var ctx = _factory.CreateDbContext();

        return (from o in ctx.Overrides.AsNoTracking()
                join s in ctx.Schedules.AsNoTracking() on o.DomainCode equals s.DomainCode
                join d in ctx.Domains.AsNoTracking()   on o.DomainCode equals d.DomainCode
                join b in ctx.Brands.AsNoTracking()    on o.BrandCode  equals b.BrandCode
                where o.DomainCode == domainCode
                   && o.BrandCode  == brandCode
                select new TargetRow
                {
                    DomainCode       = o.DomainCode,
                    BrandCode        = o.BrandCode,
                    CenterCode       = o.CenterCode ?? "",
                    PayloadJson      = o.PayloadJson ?? s.PayloadJson,
                    TimeoutSec       = o.TimeoutSec  ?? s.TimeoutSec,
                    DeprecateSec     = o.DeprecateSec ?? s.DeprecateSec,
                    TimezoneId       = o.TimezoneId  ?? s.TimezoneId,
                    // 수동 실행은 cron narrowing 을 무시하므로 CronExprs 는 빈 목록(기본값) 그대로.
                    JobType          = d.JobType,
                    DefaultQueue     = o.Queue ?? d.DefaultQueue,
                    // 실효 IsTest = Domain.TEST_YN OR Brand.TEST_YN OR (매핑된 그룹 중 TEST_YN='Y' 1개 이상).
                    // EF Core 가 SQL CASE + EXISTS 로 번역하며, 그룹 0개면 EXISTS 가 false 라 vacuously-false.
                    IsTest           = d.TestYn == "Y"
                                    || b.TestYn == "Y"
                                    || ctx.DomainGroupMaps
                                          .Where(m => m.DomainCode == d.DomainCode)
                                          .Join(ctx.DomainGroups, m => m.GroupCode, g => g.GroupCode, (m, g) => g)
                                          .Any(g => g.TestYn == "Y"),
                    // 시스템(개발팀) 메일 게이트 = 도메인 MAIL_SEND 만.
                    SystemMailSend   = d.MailSend == "Y",
                    // 브랜드 담당자 메일 게이트 = 도메인 AND 브랜드(마스터) AND 오버라이드 (셋 다 'Y').
                    BrandMailSend    = d.MailSend == "Y" && b.MailSend == "Y" && o.MailSend == "Y"
                })
                .FirstOrDefault();
    }

    /// <summary>
    /// Admin "Run domain now" 전용 — 도메인에 매핑된 모든 브랜드 오버라이드를 한 번에 조회.
    /// 그룹 활성 / 날짜 / RunAuto / 브랜드·오버라이드 USE_YN 필터 모두 우회. SCH_DOMAIN.USE_YN='N'
    /// 가드는 Dispatcher.EnqueueDomain 이 별도로 적용하므로 본 조회는 도메인 USE_YN 도 무시.
    /// 도메인이나 매핑이 없으면 빈 리스트.
    /// </summary>
    public IReadOnlyList<TargetRow> GetTargetsForManualDomainRun(string domainCode)
    {
        using var ctx = _factory.CreateDbContext();

        return (from o in ctx.Overrides.AsNoTracking()
                join s in ctx.Schedules.AsNoTracking() on o.DomainCode equals s.DomainCode
                join d in ctx.Domains.AsNoTracking()   on o.DomainCode equals d.DomainCode
                join b in ctx.Brands.AsNoTracking()    on o.BrandCode  equals b.BrandCode
                where o.DomainCode == domainCode
                orderby o.BrandCode
                select new TargetRow
                {
                    DomainCode       = o.DomainCode,
                    BrandCode        = o.BrandCode,
                    CenterCode       = o.CenterCode ?? "",
                    PayloadJson      = o.PayloadJson ?? s.PayloadJson,
                    TimeoutSec       = o.TimeoutSec  ?? s.TimeoutSec,
                    DeprecateSec     = o.DeprecateSec ?? s.DeprecateSec,
                    TimezoneId       = o.TimezoneId  ?? s.TimezoneId,
                    // 수동 실행은 cron narrowing 을 무시하므로 CronExprs 는 빈 목록(기본값) 그대로.
                    JobType          = d.JobType,
                    DefaultQueue     = o.Queue ?? d.DefaultQueue,
                    // 실효 IsTest = Domain.TEST_YN OR Brand.TEST_YN OR (매핑된 그룹 중 TEST_YN='Y' 1개 이상).
                    // EF Core 가 SQL CASE + EXISTS 로 번역하며, 그룹 0개면 EXISTS 가 false 라 vacuously-false.
                    IsTest           = d.TestYn == "Y"
                                    || b.TestYn == "Y"
                                    || ctx.DomainGroupMaps
                                          .Where(m => m.DomainCode == d.DomainCode)
                                          .Join(ctx.DomainGroups, m => m.GroupCode, g => g.GroupCode, (m, g) => g)
                                          .Any(g => g.TestYn == "Y"),
                    // 시스템(개발팀) 메일 게이트 = 도메인 MAIL_SEND 만.
                    SystemMailSend   = d.MailSend == "Y",
                    // 브랜드 담당자 메일 게이트 = 도메인 AND 브랜드(마스터) AND 오버라이드 (셋 다 'Y').
                    BrandMailSend    = d.MailSend == "Y" && b.MailSend == "Y" && o.MailSend == "Y"
                })
                .ToList();
    }

    /// <summary>
    /// Dispatcher — 활성 브랜드 타겟 목록을 EF Core LINQ 로 조회.
    /// SCH_DOMAIN_BRAND_OVERRIDE × SCH_DOMAIN_SCHEDULE × SCH_DOMAIN × SCH_BRAND 4-way JOIN.
    /// L2(오버라이드) 값이 있으면 우선, 없으면 L1(도메인 기본) 상속.
    /// 그룹 활성 검사는 위 NOT EXISTS 술어로 d 단계에서 처리 (M:N 전환).
    /// </summary>
    public IReadOnlyList<TargetRow> GetActiveBrandTargets(string domainCode, DateTime bucketedUtc)
    {
        using var ctx = _factory.CreateDbContext();

        // bucketedUtc 는 KST 단일 시간대 정책에 따라 이미 서버 로컬(KST) 시각이다 (Dispatcher.TruncateToMinute).
        // VALID_FROM/VALID_TO 도 KST 기준이므로 별도 시간대 변환 없이 날짜 부분만 추출.
        var today = DateOnly.FromDateTime(bucketedUtc);

        return (from o in ctx.Overrides.AsNoTracking()
                join s in ctx.Schedules.AsNoTracking() on o.DomainCode equals s.DomainCode
                join d in ctx.Domains.AsNoTracking()   on o.DomainCode equals d.DomainCode
                join b in ctx.Brands.AsNoTracking()    on o.BrandCode  equals b.BrandCode
                where o.DomainCode == domainCode
                   && d.UseYn      == "Y"
                   && !ctx.DomainGroupMaps
                          .Where(m => m.DomainCode == d.DomainCode)
                          .Join(ctx.DomainGroups, m => m.GroupCode, g => g.GroupCode, (m, g) => g)
                          .Any(g => g.UseYn == "N")
                   && s.UseYn  == "Y"
                   && o.UseYn      == "Y"
                   && b.UseYn      == "Y"
                   && (s.ValidFrom == null || s.ValidFrom <= today)
                   && (s.ValidTo   == null || s.ValidTo   >= today)
                   && (o.ValidFrom == null || o.ValidFrom <= today)
                   && (o.ValidTo   == null || o.ValidTo   >= today)
                select new TargetRow
                {
                    DomainCode       = o.DomainCode,
                    BrandCode        = o.BrandCode,
                    CenterCode       = o.CenterCode ?? "",
                    PayloadJson      = o.PayloadJson ?? s.PayloadJson,
                    TimeoutSec       = o.TimeoutSec  ?? s.TimeoutSec,
                    DeprecateSec     = o.DeprecateSec ?? s.DeprecateSec,
                    TimezoneId       = o.TimezoneId  ?? s.TimezoneId,
                    // 활성 시간대 cron 목록 (USE_YN='Y' + VALID 범위 today). 0개면 Dispatcher 가 L1 상속.
                    CronExprs        = ctx.BrandCrons
                                          .Where(c => c.DomainCode == o.DomainCode
                                                   && c.BrandCode  == o.BrandCode
                                                   && c.UseYn == "Y"
                                                   && (c.ValidFrom == null || c.ValidFrom <= today)
                                                   && (c.ValidTo   == null || c.ValidTo   >= today))
                                          .Select(c => c.CronExpr)
                                          .ToList(),
                    JobType          = d.JobType,
                    DefaultQueue     = o.Queue ?? d.DefaultQueue,
                    // 실효 IsTest = Domain.TEST_YN OR Brand.TEST_YN OR (매핑된 그룹 중 TEST_YN='Y' 1개 이상).
                    // EF Core 가 SQL CASE + EXISTS 로 번역하며, 그룹 0개면 EXISTS 가 false 라 vacuously-false.
                    IsTest           = d.TestYn == "Y"
                                    || b.TestYn == "Y"
                                    || ctx.DomainGroupMaps
                                          .Where(m => m.DomainCode == d.DomainCode)
                                          .Join(ctx.DomainGroups, m => m.GroupCode, g => g.GroupCode, (m, g) => g)
                                          .Any(g => g.TestYn == "Y"),
                    // 시스템(개발팀) 메일 게이트 = 도메인 MAIL_SEND 만.
                    SystemMailSend   = d.MailSend == "Y",
                    // 브랜드 담당자 메일 게이트 = 도메인 AND 브랜드(마스터) AND 오버라이드 (셋 다 'Y').
                    BrandMailSend    = d.MailSend == "Y" && b.MailSend == "Y" && o.MailSend == "Y"
                })
                .ToList();
    }
}

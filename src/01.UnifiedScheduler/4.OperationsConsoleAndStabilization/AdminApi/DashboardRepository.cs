using Cronos;
using Microsoft.EntityFrameworkCore;
using Portfolio.Admin.Data;
using Portfolio.Admin.Models;

namespace Portfolio.Admin.Infrastructure;

/// <summary>
/// 관리자 대시보드용 집계 전용 read-only repository. 화면 한 번 진입에 필요한 모든 카드 값을 단일
/// <see cref="SchedulerHubDbContext"/> 스코프에서 산출한다.
///
/// <para>
/// 각 카드를 개별 엔드포인트로 쪼개지 않은 이유 — (1) 화면 진입 직후 N번의 라운드트립 회피,
/// (2) 자정 / 현재시각 같은 기준값을 한 번만 캡쳐해 카드 간 정합성을 깨지 않기 위함.
/// </para>
/// <para>
/// Dispatch 일시정지 상태는 Scheduler 솔루션의 파일/메모리 캐시 싱글톤(<c>DispatchPauseStore</c>) 소유라
/// 본 repo 는 다루지 않는다. 프론트가 <c>/api/admin/dispatch-pause</c> 와 본 endpoint 를 병렬 호출해 합성.
/// </para>
/// </summary>
public class DashboardRepository
{
    private readonly IDbContextFactory<SchedulerHubDbContext> _factory;

    public DashboardRepository(IDbContextFactory<SchedulerHubDbContext> factory)
    {
        _factory = factory;
    }

    public DashboardSummaryDto GetSummary()
    {
        var now      = DatetimeHelper.Now;
        var todayKst = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var since24h = now.AddHours(-24);

        using var ctx = _factory.CreateDbContext();

        return new DashboardSummaryDto
        {
            ActiveCounts   = QueryActiveCounts(ctx),
            InFlight       = QueryInFlight(ctx),
            Today          = QueryTodayCounts(ctx, todayKst),
            HourlyBuckets  = QueryHourlyBuckets(ctx, now),
            RecentFailures = QueryRecentFailures(ctx, since24h),
            StuckRunning   = QueryStuckRunning(ctx, now),
            TestModeFlags  = QueryTestModeFlags(ctx),
            QueueLoad      = QueryQueueLoad(ctx),
            UpcomingRuns   = QueryUpcomingRuns(ctx),
            SnapshotDt     = now,
        };
    }

    private static DashboardActiveCountsDto QueryActiveCounts(SchedulerHubDbContext ctx)
    {
        var domains = ctx.Domains.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total  = g.Count(),
                Active = g.Count(d => d.UseYn == "Y" && d.Schedule != null && d.Schedule.UseYn == "Y"),
            })
            .FirstOrDefault() ?? new { Total = 0, Active = 0 };

        var brands = ctx.Brands.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total  = g.Count(),
                Active = g.Count(b => b.UseYn == "Y"),
            })
            .FirstOrDefault() ?? new { Total = 0, Active = 0 };

        var overrides = ctx.Overrides.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total  = g.Count(),
                Active = g.Count(o => o.UseYn == "Y"),
            })
            .FirstOrDefault() ?? new { Total = 0, Active = 0 };

        return new DashboardActiveCountsDto
        {
            ActiveDomains   = domains.Active,
            TotalDomains    = domains.Total,
            ActiveBrands    = brands.Active,
            TotalBrands     = brands.Total,
            ActiveOverrides = overrides.Active,
            TotalOverrides  = overrides.Total,
        };
    }

    private static DashboardInFlightDto QueryInFlight(SchedulerHubDbContext ctx)
    {
        var inFlightStatuses = new[] { JobStatus.ENQUEUED, JobStatus.RUNNING };

        var counts = ctx.JobLogs.AsNoTracking()
            .Where(r => inFlightStatuses.Contains(r.Status))
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Cnt = g.Count() })
            .ToList();

        var enqueued = counts.FirstOrDefault(c => c.Status == JobStatus.ENQUEUED)?.Cnt ?? 0;
        var running  = counts.FirstOrDefault(c => c.Status == JobStatus.RUNNING )?.Cnt ?? 0;

        var items = ctx.JobLogs.AsNoTracking()
            .Where(r => inFlightStatuses.Contains(r.Status))
            .OrderBy(r => r.ScheduledDt)
            .Take(20)
            .Select(r => ProjectJobLog(r))
            .ToList();

        return new DashboardInFlightDto
        {
            Total    = enqueued + running,
            Enqueued = enqueued,
            Running  = running,
            Items    = items,
        };
    }

    private static DashboardTodayCountsDto QueryTodayCounts(SchedulerHubDbContext ctx, DateTime todayKst)
    {
        var byStatus = ctx.JobLogs.AsNoTracking()
            .Where(r => r.ScheduledDt >= todayKst && r.TestYn != "Y")
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Cnt = g.Count() })
            .ToList();

        int Count(JobStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Cnt ?? 0;
        int success    = Count(JobStatus.SUCCESS);
        int failed     = Count(JobStatus.FAILED);
        int deprecated = Count(JobStatus.DEPRECATED);
        int skippedDup = Count(JobStatus.SKIPPED_DUP);
        int enqueued   = Count(JobStatus.ENQUEUED);
        int running    = Count(JobStatus.RUNNING);

        return new DashboardTodayCountsDto
        {
            Total      = success + failed + deprecated + skippedDup + enqueued + running,
            Success    = success,
            Failed     = failed,
            Deprecated = deprecated,
            SkippedDup = skippedDup,
        };
    }

    private static IReadOnlyList<DashboardHourlyBucketDto> QueryHourlyBuckets(
        SchedulerHubDbContext ctx, DateTime now)
    {
        // 최근 24h 를 "정시(hour) 경계"로 정렬한 24개 버킷으로 집계한다.
        // now.AddHours(-24) 처럼 비정시(예: 07:56) 에서 시작하면 맨 앞 버킷이 그 시(hour)의
        // 일부 구간(07:56~08:00)만 집계되고, now 가 흐를수록 그 시작 경계가 밀려 맨 앞 막대가
        // 분 단위로 줄어드는 착시가 생긴다. 그래서 창을 항상 정시로 내림(floor)하고 그 정시부터 필터.
        var endHour   = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Unspecified);
        var startHour = endHour.AddHours(-23);   // endHour 포함 정확히 24개 버킷 (앞 23개는 완결, 마지막은 진행 중인 현재 시)

        var statuses = new[] { JobStatus.SUCCESS, JobStatus.FAILED, JobStatus.SKIPPED_DUP };

        var raw = ctx.JobLogs.AsNoTracking()
            .Where(r => r.FinishedDt != null
                     && r.FinishedDt >= startHour
                     && statuses.Contains(r.Status)
                     && r.TestYn != "Y")
            .Select(r => new { r.Status, Finished = r.FinishedDt!.Value })
            .ToList();

        var buckets = new List<DashboardHourlyBucketDto>(24);
        for (var h = startHour; h <= endHour; h = h.AddHours(1))
            buckets.Add(new DashboardHourlyBucketDto { Hour = h });

        var indexByHour = buckets
            .Select((b, i) => (b.Hour, i))
            .ToDictionary(x => x.Hour, x => x.i);

        foreach (var r in raw)
        {
            var hr = new DateTime(r.Finished.Year, r.Finished.Month, r.Finished.Day, r.Finished.Hour, 0, 0, DateTimeKind.Unspecified);
            if (!indexByHour.TryGetValue(hr, out var idx)) continue;
            var b = buckets[idx];
            switch (r.Status)
            {
                case JobStatus.SUCCESS:     b.Success++;    break;
                case JobStatus.FAILED:      b.Failed++;     break;
                case JobStatus.SKIPPED_DUP: b.SkippedDup++; break;
            }
        }

        return buckets;
    }

    private static IReadOnlyList<JobLogDto> QueryRecentFailures(SchedulerHubDbContext ctx, DateTime since)
    {
        return ctx.JobLogs.AsNoTracking()
            .Where(r => r.Status == JobStatus.FAILED
                     && r.FinishedDt != null
                     && r.FinishedDt >= since)
            .OrderByDescending(r => r.FinishedDt)
            .Take(10)
            .Select(r => ProjectJobLog(r))
            .ToList();
    }

    private static IReadOnlyList<JobLogDto> QueryStuckRunning(SchedulerHubDbContext ctx, DateTime now)
    {
        // (now - StartedDt) seconds * 5 > TimeoutSec * 4   ⇒  경과 > 80% of TimeoutSec.
        var rows = ctx.JobLogs.AsNoTracking()
            .Where(r => r.Status == JobStatus.RUNNING && r.StartedDt != null)
            .Select(r => new
            {
                Row        = r,
                ElapsedSec = (int)EF.Functions.DateDiffSecond(r.StartedDt!.Value, now),
            })
            .Where(x => x.ElapsedSec * 5 > x.Row.TimeoutSec * 4)
            .OrderBy(x => x.Row.StartedDt)
            .Take(20)
            .Select(x => ProjectJobLog(x.Row))
            .ToList();

        return rows;
    }

    private static DashboardTestModeFlagsDto QueryTestModeFlags(SchedulerHubDbContext ctx)
    {
        return new DashboardTestModeFlagsDto
        {
            Domains = ctx.Domains.AsNoTracking()
                .Where(d => d.TestYn == "Y")
                .OrderBy(d => d.DomainCode)
                .Select(d => new TestModeEntityDto { Code = d.DomainCode, Label = d.Remark })
                .ToList(),
            Brands = ctx.Brands.AsNoTracking()
                .Where(b => b.TestYn == "Y")
                .OrderBy(b => b.BrandCode)
                .Select(b => new TestModeEntityDto { Code = b.BrandCode, Label = b.Description })
                .ToList(),
            Groups = ctx.DomainGroups.AsNoTracking()
                .Where(g => g.TestYn == "Y")
                .OrderBy(g => g.GroupCode)
                .Select(g => new TestModeEntityDto { Code = g.GroupCode, Label = g.Description })
                .ToList(),
        };
    }

    private static IReadOnlyList<DashboardQueueLoadDto> QueryQueueLoad(SchedulerHubDbContext ctx)
    {
        var domainQueue = ctx.Domains.AsNoTracking()
            .Select(d => new { d.DomainCode, d.DefaultQueue })
            .ToDictionary(x => x.DomainCode, x => x.DefaultQueue);

        var queues = ctx.Queues.AsNoTracking()
            .OrderBy(q => q.QueueName)
            .Select(q => new { q.QueueName, q.WorkerCount, q.UseYn })
            .ToList();

        var inFlightStatuses = new[] { JobStatus.ENQUEUED, JobStatus.RUNNING };
        var inFlight = ctx.JobLogs.AsNoTracking()
            .Where(r => inFlightStatuses.Contains(r.Status))
            .Select(r => new { r.DomainCode, r.Status })
            .ToList();

        var perQueue = queues.ToDictionary(
            q => q.QueueName,
            q => new DashboardQueueLoadDto
            {
                QueueName   = q.QueueName,
                WorkerCount = q.WorkerCount,
                UseYn       = q.UseYn,
            });

        foreach (var row in inFlight)
        {
            if (!domainQueue.TryGetValue(row.DomainCode, out var queueName)) continue;
            if (!perQueue.TryGetValue(queueName, out var bucket))            continue;
            if (row.Status == JobStatus.ENQUEUED) bucket.Enqueued++;
            else                                  bucket.Running++;
        }

        return perQueue.Values
            .OrderBy(q => q.QueueName)
            .ToList();
    }

    private static IReadOnlyList<DashboardUpcomingRunDto> QueryUpcomingRuns(SchedulerHubDbContext ctx)
        // 대시보드 카드 = 전역(brand/domain 필터 없음) Top 100.
        => ComputeUpcoming(ctx, ActiveUpcomingCandidates(ctx, null, null), 100);

    // ──────────────────────────────────────────────
    //  다음 실행 예정 — 발사 후보 & 시각 계산 (대시보드 카드 + next-runs 화면 공용)
    // ──────────────────────────────────────────────

    /// <summary>발사 후보 1행 — 활성 필터를 통과한 (Domain × Brand) override.</summary>
    private sealed record UpcomingCandidate(
        string  DomainCode,
        string  BrandCode,
        string  DomainCron,
        string? Remark,
        string? BrandDescription,
        string? OverrideQueue,
        string  DefaultQueue,
        string  JobType,
        string? ParamDtoType);

    /// <summary>
    /// dispatcher Fire(cron 자동 발사) 의 활성 필터를 그대로 미러링한 발사 후보 목록.
    ///   - Domain/Brand/Override/Schedule 모두 USE_YN='Y'
    ///   - Domain/Brand 모두 TEST_YN!='Y' (그룹 단위 TEST_YN='Y' / USE_YN='N' 은 별도 제외)
    ///   - Schedule.ValidFrom/ValidTo 및 Override.ValidFrom/ValidTo 가 today(KST) 포함
    /// <paramref name="brandFilter"/>/<paramref name="domainFilter"/> 가 주어지면 그 축으로 추가 좁힘.
    /// <paramref name="asOf"/> 는 유효성(VALID_FROM/TO) 판정 기준일 — 기본은 오늘(KST). "실행 결과 현황"
    /// 처럼 과거 일자를 조회할 땐 그 일자를 넘겨 그 시점의 활성 후보를 근사한다(USE_YN 등 현재값은 근사).
    /// </summary>
    private static List<UpcomingCandidate> ActiveUpcomingCandidates(
        SchedulerHubDbContext ctx, string? brandFilter, string? domainFilter, DateOnly? asOf = null)
    {
        var todayKst = asOf ?? DateOnly.FromDateTime(DatetimeHelper.Now);

        var rows = (
            from o in ctx.Overrides.AsNoTracking()
            join d in ctx.Domains.AsNoTracking()   on o.DomainCode equals d.DomainCode
            join b in ctx.Brands.AsNoTracking()    on o.BrandCode  equals b.BrandCode
            join s in ctx.Schedules.AsNoTracking() on o.DomainCode equals s.DomainCode
            where o.UseYn  == "Y"
               && d.UseYn  == "Y"
               && b.UseYn  == "Y"
               && d.TestYn != "Y"
               && b.TestYn != "Y"
               && s.UseYn  == "Y"
               // Schedule validity (양쪽 null 허용 = 무제한).
               && (s.ValidFrom == null || s.ValidFrom <= todayKst)
               && (s.ValidTo   == null || s.ValidTo   >= todayKst)
               // Override validity (양쪽 null 허용 = 무제한).
               && (o.ValidFrom == null || o.ValidFrom <= todayKst)
               && (o.ValidTo   == null || o.ValidTo   >= todayKst)
               && (brandFilter  == null || o.BrandCode  == brandFilter)
               && (domainFilter == null || o.DomainCode == domainFilter)
            select new UpcomingCandidate(
                o.DomainCode,
                o.BrandCode,
                s.CronExpr,
                d.Remark,
                b.Description,
                // 실효 큐: override.QUEUE 우선, 비면 도메인 DEFAULT_QUEUE (디스패처 enqueue 와 동일).
                o.Queue,
                d.DefaultQueue,
                d.JobType,
                d.ParamDtoType))
            .ToList();

        if (rows.Count == 0)
            return rows;

        // 그룹 매핑 중 TEST_YN='Y' (→ effectiveIsTest=true) 또는 USE_YN='N' (→ dispatch skip) 인
        // 그룹에 1개라도 걸린 도메인은 제외. Distinct DomainCode 셋이라 작음.
        var excludeDomains = ctx.DomainGroupMaps.AsNoTracking()
            .Join(ctx.DomainGroups.AsNoTracking().Where(g => g.TestYn == "Y" || g.UseYn == "N"),
                  m => m.GroupCode, g => g.GroupCode, (m, _) => m.DomainCode)
            .Distinct()
            .ToHashSet();

        return rows.Where(r => !excludeDomains.Contains(r.DomainCode)).ToList();
    }

    /// <summary>
    /// 발사 후보 목록에서 다음 발사 시각을 Cronos 로 산출해 NextDt asc Top <paramref name="topN"/> 반환.
    /// 동일 (Domain × Brand) 페어가 5분마다 발사면 시간순으로 여러 번 등장한다 (페어 단위 unique 아님).
    /// </summary>
    private static List<DashboardUpcomingRunDto> ComputeUpcoming(
        SchedulerHubDbContext ctx, List<UpcomingCandidate> candidates, int topN)
    {
        if (candidates.Count == 0)
            return new List<DashboardUpcomingRunDto>();

        var todayKst    = DateOnly.FromDateTime(DatetimeHelper.Now);
        var domainCodes = candidates.Select(c => c.DomainCode).Distinct().ToList();

        // 시간대별 cron(SCH_DOMAIN_BRAND_CRON) 활성 목록을 (도메인,브랜드) 단위로 로드.
        // dispatcher 의 IsAnyFireTime 와 동일하게 "이 중 하나라도 도메인 tick 에 매치되면 발사(OR)".
        // 비어있으면 도메인 L1 스케줄 상속(매 tick 발사).
        var cronsByPair = ctx.BrandCrons.AsNoTracking()
            .Where(c => c.UseYn == "Y"
                     && domainCodes.Contains(c.DomainCode)
                     && (c.ValidFrom == null || c.ValidFrom <= todayKst)
                     && (c.ValidTo   == null || c.ValidTo   >= todayKst))
            .Select(c => new { c.DomainCode, c.BrandCode, c.CronExpr })
            .ToList()
            .GroupBy(c => (c.DomainCode, c.BrandCode))
            .ToDictionary(g => g.Key, g => g.Select(x => x.CronExpr).ToList());

        var kst    = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var nowUtc = DateTime.UtcNow;

        // 페어당 최대 생성 fire 수 — 한 페어가 결과 전체를 다 차지하는 극단 케이스 대비. 글로벌
        // sort 후 Take(topN) 으로 최종 컷. (단일 페어 조회 시엔 그 페어 topN 개를 그대로 보장.)
        var maxFiresPerPair = topN;
        // 페어당 도메인 cron tick 최대 lookahead — override 가 narrowing 으로 매우 드물게 매치되는
        // 케이스에서 무한 루프 방지. 분 단위 cron 기준 ~3.5일 분량.
        const int MaxLookaheadPerPair = 5000;

        var results = new List<DashboardUpcomingRunDto>(candidates.Count * 4);
        foreach (var row in candidates)
        {
            if (string.IsNullOrWhiteSpace(row.DomainCron)) continue;

            CronExpression domainExpr;
            try { domainExpr = CronExpression.Parse(row.DomainCron); }
            catch { continue; }

            // 활성 시간대 cron 들을 (식 문자열, 파싱결과) 로 준비. 파싱 실패한 cron 은 건너뜀
            // (입력 단계에서 문법 검증하므로 정상 흐름엔 없음). 0개면 도메인 L1 상속.
            var overrideExprs = new List<(string Expr, CronExpression Cron)>();
            if (cronsByPair.TryGetValue((row.DomainCode, row.BrandCode), out var cronStrs))
            {
                foreach (var cs in cronStrs)
                {
                    if (string.IsNullOrWhiteSpace(cs)) continue;
                    try { overrideExprs.Add((cs, CronExpression.Parse(cs))); }
                    catch { /* skip malformed */ }
                }
            }

            // dispatcher 의 fire 시점은 "도메인 cron tick 중 시간대 cron 도 같은 분에 tick 하는 시각".
            // 시간대 cron 이 없으면 도메인 cron tick 이 그대로 fire 시각(L1 상속).
            // 페어당 maxFiresPerPair 개까지 generate, lookahead 캡 MaxLookaheadPerPair.
            var pairFires = 0;
            var cursor    = nowUtc;
            for (var i = 0; i < MaxLookaheadPerPair && pairFires < maxFiresPerPair; i++)
            {
                var nextDomain = domainExpr.GetNextOccurrence(cursor, kst);
                if (nextDomain == null) break;
                cursor = nextDomain.Value;

                // 화면 표시용 cron: 시간대 cron 이 없으면 도메인 식.
                var effectiveCron = row.DomainCron;

                if (overrideExprs.Count > 0)
                {
                    // Dispatcher.IsAnyFireTime 시멘틱: 시간대 cron 중 하나라도 이번 tick 에 매치(OR)하면 발사.
                    // 매치된 cron 식을 표시용으로 채택(겹치면 첫 매치).
                    string? matched = null;
                    foreach (var (expr, cron) in overrideExprs)
                    {
                        var prev = cron.GetNextOccurrence(nextDomain.Value.AddMinutes(-1), kst);
                        if (prev.HasValue && prev.Value == nextDomain.Value) { matched = expr; break; }
                    }
                    if (matched == null) continue;
                    effectiveCron = matched;
                }

                var nextKst = TimeZoneInfo.ConvertTimeFromUtc(nextDomain.Value, kst);
                results.Add(new DashboardUpcomingRunDto
                {
                    DomainCode   = row.DomainCode,
                    Remark       = row.Remark,
                    BrandCode    = row.BrandCode,
                    CronExpr     = effectiveCron,
                    NextDt       = DateTime.SpecifyKind(nextKst, DateTimeKind.Unspecified),
                    Queue        = string.IsNullOrWhiteSpace(row.OverrideQueue) ? row.DefaultQueue : row.OverrideQueue,
                    JobType      = row.JobType,
                    ParamDtoType = row.ParamDtoType,
                });
                pairFires++;
            }
        }

        return results
            .OrderBy(r => r.NextDt)
            .Take(topN)
            .ToList();
    }

    // ──────────────────────────────────────────────
    //  다음 실행 예정 브라우저(next-runs 화면) — 브랜드 → 도메인 → 시각 드릴다운
    // ──────────────────────────────────────────────

    /// <summary>활성 발사 후보(override) 를 1건 이상 가진 브랜드 목록 (코드 asc).</summary>
    public IReadOnlyList<NextRunBrandDto> GetUpcomingBrands()
    {
        using var ctx = _factory.CreateDbContext();
        return ActiveUpcomingCandidates(ctx, null, null)
            .GroupBy(c => c.BrandCode)
            .Select(g => new NextRunBrandDto
            {
                BrandCode   = g.Key,
                Description = g.Select(x => x.BrandDescription).FirstOrDefault(),
                DomainCount = g.Select(x => x.DomainCode).Distinct().Count(),
            })
            .OrderBy(b => b.BrandCode)
            .ToList();
    }

    /// <summary>선택 브랜드가 활성 발사 후보(override) 를 가진 도메인 목록 (코드 asc).</summary>
    public IReadOnlyList<NextRunDomainDto> GetUpcomingDomainsForBrand(string brandCode)
    {
        if (string.IsNullOrWhiteSpace(brandCode))
            return Array.Empty<NextRunDomainDto>();

        using var ctx = _factory.CreateDbContext();
        return ActiveUpcomingCandidates(ctx, brandCode, null)
            .GroupBy(c => c.DomainCode)
            .Select(g => new NextRunDomainDto
            {
                DomainCode = g.Key,
                Remark     = g.Select(x => x.Remark).FirstOrDefault(),
            })
            .OrderBy(d => d.DomainCode)
            .ToList();
    }

    /// <summary>활성 발사 후보(override) 를 1건 이상 가진 도메인 목록 (코드 asc). 도메인 기준 탭의 좌측 col.</summary>
    public IReadOnlyList<NextRunDomainDto> GetUpcomingDomains()
    {
        using var ctx = _factory.CreateDbContext();
        return ActiveUpcomingCandidates(ctx, null, null)
            .GroupBy(c => c.DomainCode)
            .Select(g => new NextRunDomainDto
            {
                DomainCode = g.Key,
                Remark     = g.Select(x => x.Remark).FirstOrDefault(),
                BrandCount = g.Select(x => x.BrandCode).Distinct().Count(),
            })
            .OrderBy(d => d.DomainCode)
            .ToList();
    }

    /// <summary>선택 도메인이 활성 발사 후보(override) 를 가진 브랜드 목록 (코드 asc).</summary>
    public IReadOnlyList<NextRunBrandDto> GetUpcomingBrandsForDomain(string domainCode)
    {
        if (string.IsNullOrWhiteSpace(domainCode))
            return Array.Empty<NextRunBrandDto>();

        using var ctx = _factory.CreateDbContext();
        return ActiveUpcomingCandidates(ctx, null, domainCode)
            .GroupBy(c => c.BrandCode)
            .Select(g => new NextRunBrandDto
            {
                BrandCode   = g.Key,
                Description = g.Select(x => x.BrandDescription).FirstOrDefault(),
            })
            .OrderBy(b => b.BrandCode)
            .ToList();
    }

    /// <summary>
    /// 다음 발사 예정 시각 Top <paramref name="top"/>. <paramref name="brandCode"/>/<paramref name="domainCode"/>
    /// 둘 중 최소 1개는 있어야 한다(둘 다 없으면 빈 목록). 한쪽만 주면 그 축 전체 합산.
    /// </summary>
    public IReadOnlyList<DashboardUpcomingRunDto> GetUpcomingRuns(string? brandCode, string? domainCode, int top)
    {
        var brand  = string.IsNullOrWhiteSpace(brandCode)  ? null : brandCode;
        var domain = string.IsNullOrWhiteSpace(domainCode) ? null : domainCode;
        if (brand == null && domain == null)
            return Array.Empty<DashboardUpcomingRunDto>();
        if (top <= 0) top = 20;

        using var ctx = _factory.CreateDbContext();
        return ComputeUpcoming(ctx, ActiveUpcomingCandidates(ctx, brand, domain), top);
    }

    // ──────────────────────────────────────────────
    //  실행 결과 현황(run-status 화면) — (일자 + 시) 윈도우의 예상 발사 × 실제 로그 대조
    // ──────────────────────────────────────────────

    /// <summary>
    /// 선택한 (<paramref name="date"/> + <paramref name="hour"/>) 1시간 윈도우에서 발사됐어야 할
    /// (Domain × Brand) 예상 발사 목록을 Cronos 로 산출하고, 같은 윈도우의 <c>SCH_JOB_LOG</c>
    /// (운영/non-test) 와 <c>SCHEDULED_DT</c>(분 단위) exact-equal 로 좌조인해 실행 결과를 합성한다.
    /// <para>
    /// 발사 후보/시간대 cron 협소화 로직은 대시보드 "다음 실행 예정" 과 동일 미러(활성 필터는 <paramref name="date"/>
    /// 기준). 로그가 없으면 예상시각이 과거면 <c>MISSED</c>, 미래면 <c>SCHEDULED</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<RunStatusRowDto> GetRunStatus(DateOnly date, int hour, string? brandCode, string? domainCode)
    {
        var brand  = string.IsNullOrWhiteSpace(brandCode)  ? null : brandCode;
        var domain = string.IsNullOrWhiteSpace(domainCode) ? null : domainCode;
        if (hour < 0)  hour = 0;
        if (hour > 23) hour = 23;

        var now         = DatetimeHelper.Now;                                  // KST, unspecified
        var windowStart = date.ToDateTime(new TimeOnly(hour, 0));              // KST, unspecified
        var windowEnd   = windowStart.AddHours(1);                            // exclusive

        using var ctx = _factory.CreateDbContext();

        var candidates = ActiveUpcomingCandidates(ctx, brand, domain, date);
        if (candidates.Count == 0)
            return Array.Empty<RunStatusRowDto>();

        var domainCodes = candidates.Select(c => c.DomainCode).Distinct().ToList();

        // 시간대 cron(SCH_DOMAIN_BRAND_CRON) — date 기준 활성. dispatcher IsAnyFireTime(OR) 미러.
        var cronsByPair = ctx.BrandCrons.AsNoTracking()
            .Where(c => c.UseYn == "Y"
                     && domainCodes.Contains(c.DomainCode)
                     && (c.ValidFrom == null || c.ValidFrom <= date)
                     && (c.ValidTo   == null || c.ValidTo   >= date))
            .Select(c => new { c.DomainCode, c.BrandCode, c.CronExpr })
            .ToList()
            .GroupBy(c => (c.DomainCode, c.BrandCode))
            .ToDictionary(g => g.Key, g => g.Select(x => x.CronExpr).ToList());

        // 윈도우 로그 1회 SELECT (운영만). 같은 (Domain,Brand,ScheduledDt) 로 인덱싱해 좌조인.
        // 동일 키 중복(이례적)이면 EnqueuedDt 최신 1건 채택.
        var logs = ctx.JobLogs.AsNoTracking()
            .Where(r => r.ScheduledDt >= windowStart
                     && r.ScheduledDt <  windowEnd
                     && r.TestYn != "Y"
                     && domainCodes.Contains(r.DomainCode)
                     && (brand  == null || r.BrandCode  == brand)
                     && (domain == null || r.DomainCode == domain))
            .Select(r => new
            {
                r.LogId, r.DomainCode, r.BrandCode, r.ScheduledDt,
                r.Status, r.StartedDt, r.FinishedDt, r.ErrorMessage, r.EnqueuedDt,
            })
            .ToList();

        var logByKey = logs
            .GroupBy(r => (r.DomainCode, r.BrandCode, r.ScheduledDt))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EnqueuedDt).First());

        var kst     = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(windowStart, DateTimeKind.Unspecified), kst);
        var toUtc   = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(windowEnd,   DateTimeKind.Unspecified), kst);

        var rows = new List<RunStatusRowDto>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.DomainCron)) continue;

            CronExpression domainExpr;
            try { domainExpr = CronExpression.Parse(c.DomainCron); }
            catch { continue; }

            var overrideExprs = new List<(string Expr, CronExpression Cron)>();
            if (cronsByPair.TryGetValue((c.DomainCode, c.BrandCode), out var cronStrs))
            {
                foreach (var cs in cronStrs)
                {
                    if (string.IsNullOrWhiteSpace(cs)) continue;
                    try { overrideExprs.Add((cs, CronExpression.Parse(cs))); }
                    catch { /* skip malformed */ }
                }
            }

            // 윈도우 [fromUtc, toUtc) 안의 도메인 cron tick 전개. GetNextOccurrence 는 입력 초과 최소값을
            // 반환하므로 cursor 를 fromUtc-1s 로 시작해 fromUtc 정각 발사도 포함.
            var cursor = fromUtc.AddSeconds(-1);
            while (true)
            {
                var nextDomain = domainExpr.GetNextOccurrence(cursor, kst);
                if (nextDomain == null || nextDomain.Value >= toUtc) break;
                cursor = nextDomain.Value;

                var effectiveCron = c.DomainCron;
                if (overrideExprs.Count > 0)
                {
                    string? matched = null;
                    foreach (var (expr, cron) in overrideExprs)
                    {
                        var prev = cron.GetNextOccurrence(nextDomain.Value.AddMinutes(-1), kst);
                        if (prev.HasValue && prev.Value == nextDomain.Value) { matched = expr; break; }
                    }
                    if (matched == null) continue;   // 이번 tick 은 이 브랜드 시간대에 미발사
                    effectiveCron = matched;
                }

                var scheduledKst = DateTime.SpecifyKind(
                    TimeZoneInfo.ConvertTimeFromUtc(nextDomain.Value, kst), DateTimeKind.Unspecified);

                var row = new RunStatusRowDto
                {
                    DomainCode       = c.DomainCode,
                    Remark           = c.Remark,
                    BrandCode        = c.BrandCode,
                    BrandDescription = c.BrandDescription,
                    ScheduledDt      = scheduledKst,
                    CronExpr         = effectiveCron,
                };

                if (logByKey.TryGetValue((c.DomainCode, c.BrandCode, scheduledKst), out var log))
                {
                    row.Status       = log.Status.ToString();
                    row.StartedDt    = log.StartedDt;
                    row.FinishedDt   = log.FinishedDt;
                    row.ErrorMessage = log.ErrorMessage;
                    row.LogId        = log.LogId;
                    if (log.StartedDt.HasValue && log.FinishedDt.HasValue)
                        row.DurationSec = (int)(log.FinishedDt.Value - log.StartedDt.Value).TotalSeconds;
                }
                else
                {
                    row.Status = scheduledKst < now ? "MISSED" : "SCHEDULED";
                }

                rows.Add(row);
            }
        }

        return rows
            .OrderBy(r => r.ScheduledDt)
            .ThenBy(r => r.DomainCode)
            .ThenBy(r => r.BrandCode)
            .ToList();
    }

    private static JobLogDto ProjectJobLog(SchJobLog r) => new()
    {
        LogId         = r.LogId,
        DomainCode    = r.DomainCode,
        BrandCode     = r.BrandCode,
        CenterCode    = r.CenterCode,
        ScheduledDt   = r.ScheduledDt,
        EnqueuedDt    = r.EnqueuedDt,
        StartedDt     = r.StartedDt,
        FinishedDt    = r.FinishedDt,
        Status        = r.Status,
        HangfireJobId = r.HangfireJobId,
        TimeoutSec    = r.TimeoutSec,
        DeprecateSec  = r.DeprecateSec,
        ErrorMessage  = r.ErrorMessage,
        TestYn        = r.TestYn,
        Requester     = r.Requester,
    };
}

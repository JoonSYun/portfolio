using Microsoft.EntityFrameworkCore;
using Portfolio.Admin.Data;
using Portfolio.Admin.Models;

namespace Portfolio.Admin.Infrastructure;

/// <summary>
/// Admin 솔루션의 잡 로그 조회 전용 repository. 상태 전이 / Hangfire 연동 메서드는 Scheduler 솔루션에만
/// 있고(워커가 호출), Admin 은 화면 조회 (현재/아카이브) 두 메서드만 보유한다.
/// FromSqlRaw + WITH (NOLOCK) 패턴은 Scheduler 측 동명 클래스와 동일 — 잡 핫 트랜잭션과 락 경합 방지.
/// </summary>
public class JobLogReadRepository
{
    private readonly IDbContextFactory<SchedulerHubDbContext> _factory;

    public JobLogReadRepository(IDbContextFactory<SchedulerHubDbContext> factory)
    {
        _factory = factory;
    }

    public PagedResult<JobLogDto> QueryLogs(JobLogQuery q)
    {
        using var ctx = _factory.CreateDbContext();

        var page = q.Page < 1 ? 1 : q.Page;
        var size = q.Size switch { < 1 => 50, > 500 => 500, _ => q.Size };

        var baseQuery = ctx.JobLogs
            .FromSqlRaw("SELECT * FROM SCH_JOB_LOG WITH (NOLOCK)")
            .AsNoTracking();

        if (q.From.HasValue)   baseQuery = baseQuery.Where(r => r.ScheduledDt >= q.From.Value);
        if (q.To.HasValue)     baseQuery = baseQuery.Where(r => r.ScheduledDt <= q.To.Value);
        if (q.Status.HasValue) baseQuery = baseQuery.Where(r => r.Status == q.Status.Value);
        if (!string.IsNullOrWhiteSpace(q.Domain))
            baseQuery = baseQuery.Where(r => r.DomainCode == q.Domain);
        if (!string.IsNullOrWhiteSpace(q.Brand))
            baseQuery = baseQuery.Where(r => r.BrandCode == q.Brand);
        if (!string.IsNullOrWhiteSpace(q.TestYn))
            baseQuery = baseQuery.Where(r => r.TestYn == q.TestYn);

        var total = baseQuery.Count();

        var items = baseQuery
            .OrderByDescending(r => r.ScheduledDt)
            .ThenByDescending(r => r.EnqueuedDt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new JobLogDto
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
            })
            .ToList();

        return new PagedResult<JobLogDto>
        {
            Items      = items,
            Pagination = new PaginationMeta { Total = total, Page = page, Size = size },
        };
    }

    /// <summary>
    /// LOG_ID 단건 조회. 상세 모달 딥링크(?logId=)용. 없으면 null.
    /// LOG_ID 는 CHAR(40) 이라 trailing space 무시 비교 — trim 된 값으로 조회해도 매치된다.
    /// </summary>
    public JobLogDto? GetById(string logId)
    {
        if (string.IsNullOrWhiteSpace(logId))
            return null;

        using var ctx = _factory.CreateDbContext();

        return ctx.JobLogs
            .FromSqlRaw("SELECT * FROM SCH_JOB_LOG WITH (NOLOCK)")
            .AsNoTracking()
            .Where(r => r.LogId == logId)
            .Select(r => new JobLogDto
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
            })
            .FirstOrDefault();
    }

    public PagedResult<JobLogDto> QueryArchive(JobLogQuery q)
    {
        using var ctx = _factory.CreateDbContext();

        var page = q.Page < 1 ? 1 : q.Page;
        var size = q.Size switch { < 1 => 50, > 500 => 500, _ => q.Size };

        var baseQuery = ctx.JobLogsBk
            .FromSqlRaw("SELECT * FROM SCH_JOB_LOG_BK WITH (NOLOCK)")
            .AsNoTracking();

        if (q.From.HasValue)   baseQuery = baseQuery.Where(r => r.ScheduledDt >= q.From.Value);
        if (q.To.HasValue)     baseQuery = baseQuery.Where(r => r.ScheduledDt <= q.To.Value);
        if (q.Status.HasValue) baseQuery = baseQuery.Where(r => r.Status == q.Status.Value);
        if (!string.IsNullOrWhiteSpace(q.Domain))
            baseQuery = baseQuery.Where(r => r.DomainCode == q.Domain);
        if (!string.IsNullOrWhiteSpace(q.Brand))
            baseQuery = baseQuery.Where(r => r.BrandCode == q.Brand);
        if (!string.IsNullOrWhiteSpace(q.TestYn))
            baseQuery = baseQuery.Where(r => r.TestYn == q.TestYn);

        var total = baseQuery.Count();

        var items = baseQuery
            .OrderByDescending(r => r.ScheduledDt)
            .ThenByDescending(r => r.EnqueuedDt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new JobLogDto
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
            })
            .ToList();

        return new PagedResult<JobLogDto>
        {
            Items      = items,
            Pagination = new PaginationMeta { Total = total, Page = page, Size = size },
        };
    }
}

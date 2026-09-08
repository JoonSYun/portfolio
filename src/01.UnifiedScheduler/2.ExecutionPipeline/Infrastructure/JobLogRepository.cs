// [담당업무 2] 상태 전이 저장소 — 모든 전이가 from-status 가드 + 행수 반환으로 멱등이며, transient SQL 오류는 Polly pipeline 이 재시도한다.

using Microsoft.EntityFrameworkCore;
using Polly;
using Portfolio.UnifiedScheduler.Data;
using Portfolio.UnifiedScheduler.Infrastructure.Resilience;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// <see cref="JobLogRepository.MarkRunningOrThrow"/> 반환값. ENQUEUED→RUNNING 전이에 실제로 성공한
/// <see cref="Running"/> 과 sweeper 가 이미 DEPRECATED 로 마감해버린 정상 race-loss 인
/// <see cref="AlreadyDeprecated"/> 를 구분한다. 그 외 비정상 상태(row 없음 / SUCCESS / FAILED /
/// 다른 워커의 RUNNING 등) 는 outcome 이 아니라 예외로 escalate 되어 호출자(<see cref="Jobs.JobBase{TSelf,TParam}"/>)
/// 의 단일 catch 에서 FAILED 마감으로 모인다.
/// </summary>
public enum MarkRunningOutcome
{
    /// <summary>ENQUEUED → RUNNING 전이에 성공. 비즈니스 로직을 진행해야 하는 정상 경로.</summary>
    Running,

    /// <summary>이미 DEPRECATED 상태였음 — sweeper 가 정상 처리한 race-loss. 비즈니스를 실행하지 않고 정상 종료한다.</summary>
    AlreadyDeprecated,
}

/// <summary>
/// <see cref="JobLogRepository.ValidateForDeprecate"/> 결과 — 수동 DEPRECATED 전이를 시작하기 전
/// 컨트롤러가 Hangfire 측 상태 검사 / 후속 UPDATE 분기를 결정하는 데 필요한 최소 정보.
/// row 검증(존재 + ENQUEUED|RUNNING) 통과를 의미하며, 검증 실패는 호출 시점에 예외로 escalate.
/// </summary>
public sealed record JobLogForDeprecate(JobStatus Status, string? HangfireJobId);

/// <summary>
/// SCH_JOB_LOG 상태 전이 + Idempotency 보장. 모든 status 전이는 from-status 가드 + 행수 반환으로
/// 멱등성을 만족하며 transient SQL 오류는 <see cref="SqlResiliencePipelineFactory"/> pipeline 이 자동 재시도.
/// </summary>
public class JobLogRepository
{
    /// <summary>SCH_JOB_LOG.ERROR_MESSAGE 컬럼 최대 길이 (V001 마이그레이션 NVARCHAR(2000) 와 일치).</summary>
    private const int ErrorMessageMaxLen = 2000;
    private const string TruncationSuffix = " ... (truncated)";

    /// <summary>
    /// stale 조회 2종(<see cref="FindStaleEnqueuedJobs"/>·<see cref="FindStaleRunningJobs"/>) 이 공유하는 투영.
    /// 두 쿼리는 판정 조건만 다르고 필요한 컬럼은 같으므로 한 곳에서 정의해 어긋남을 막는다.
    /// <see cref="System.Linq.Expressions.Expression"/> 으로 두는 이유 — 일반 메서드로 빼면 EF 가 SQL 로
    /// 번역하지 못하고 런타임에 터진다.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<SchJobLog, StaleJobRow>> StaleProjection =
        r => new StaleJobRow
        {
            LogId         = r.LogId,
            HangfireJobId = r.HangfireJobId,
            // 이하는 폐기 알림 메일 본문용 — 같은 row 라 JOIN 없이 컬럼만 더 읽는다.
            DomainCode    = r.DomainCode,
            BrandCode     = r.BrandCode,
            CenterCode    = r.CenterCode,
            ScheduledDt   = r.ScheduledDt,
            StartedDt     = r.StartedDt,
            DeprecateSec  = r.DeprecateSec,
            TimeoutSec    = r.TimeoutSec,
            TestYn        = r.TestYn,
            Requester     = r.Requester
        };

    private readonly IDbContextFactory<SchedulerHubDbContext> _factory;
    private readonly ResiliencePipeline _retry;

    public JobLogRepository(
        IDbContextFactory<SchedulerHubDbContext> factory,
        SqlResiliencePipelineFactory             pipelineFactory)
    {
        _factory = factory;
        _retry   = pipelineFactory.Pipeline;
    }

    /// <summary>
    /// Idempotency INSERT — LOG_ID UNIQUE 충돌 시 false 반환. UNIQUE 충돌은 <see cref="DbUpdateException"/> 이지만
    /// transient 가 아니므로 retry pipeline 이 즉시 흘려보내 catch 가 정상 동작한다 (race-loser 인정).
    /// </summary>
    public bool TryInsertEnqueued(JobArgs args, string? hangfireJobId)
    {
        return _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();

            if (ctx.JobLogs.Any(r => r.LogId == args.LogId)) return false;

            ctx.JobLogs.Add(new SchJobLog
            {
                LogId         = args.LogId,
                DomainCode    = args.DomainCode,
                BrandCode     = args.BrandCode,
                CenterCode    = args.CenterCode,
                ScheduledDt   = args.BucketedDt,
                EnqueuedDt    = DatetimeHelper.Now,
                Status        = JobStatus.ENQUEUED,
                HangfireJobId = hangfireJobId,
                TimeoutSec    = args.TimeoutSec,
                DeprecateSec  = args.DeprecateSec,
                TestYn        = args.IsTest ? "Y" : "N",
                Requester     = args.Requester
            });

            try
            {
                ctx.SaveChanges();
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        });
    }

    public void UpdateHangfireJobId(string logId, string hangfireJobId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            ctx.JobLogs
                .Where(r => r.LogId == logId)
                .ExecuteUpdate(s => s.SetProperty(r => r.HangfireJobId, hangfireJobId));
        });

    /// <summary>
    /// ENQUEUED → RUNNING 으로 전이 시도. 결과를 <see cref="MarkRunningOutcome"/> 으로 표현하며,
    /// 정상 race 인 DEPRECATED 만 outcome 으로 인정하고 그 외 비정상 상태는 예외로 escalate 한다.
    ///
    /// <para>
    /// 분기 의미:
    /// <list type="bullet">
    /// <item><description><see cref="MarkRunningOutcome.Running"/> — affected 1: ENQUEUED 를 RUNNING 으로 옮긴 정상 경로. 호출자는 비즈니스를 실행한다.</description></item>
    /// <item><description><see cref="MarkRunningOutcome.AlreadyDeprecated"/> — affected 0 + row STATUS=DEPRECATED: sweeper 가 timeout 등으로 마감한 알려진 race. 호출자는 비즈니스 건너뛰고 정상 종료.</description></item>
    /// <item><description>그 외 (row 없음 / SUCCESS / FAILED / 다른 워커의 RUNNING / SKIPPED_DUP): 스케줄러 정합성이 깨진 상태 — <see cref="InvalidOperationException"/> 으로 throw 해 호출자의 catch 에서 FAILED 마감 + 운영자 알림으로 이어지게 한다.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public MarkRunningOutcome MarkRunningOrThrow(string logId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();

            // [1] ENQUEUED → RUNNING 전이 시도. from-status 가드로 다른 상태의 row 는 못 건드리고 정확히 0/1 rows 만 affected.
            var updated = ctx.JobLogs
                .Where(r => r.LogId == logId && r.Status == JobStatus.ENQUEUED)
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,    JobStatus.RUNNING)
                    .SetProperty(r => r.StartedDt, DatetimeHelper.Now));
            if (updated == 1) return MarkRunningOutcome.Running;

            // [2] ENQUEUED 가 아닌 경우 — 정상 race 인지(DEPRECATED) 비정상 race 인지를 한 번 더 SELECT 로 확인.
            //     DEPRECATED 만 acceptable: sweeper 가 timeout 등으로 마감한 알려진 케이스라 알림 노이즈가 되어선 안 됨.
            var isDeprecated = ctx.JobLogs
                .AsNoTracking()
                .Any(r => r.LogId == logId && r.Status == JobStatus.DEPRECATED);
            if (isDeprecated) return MarkRunningOutcome.AlreadyDeprecated;

            // [3] 그 외 — row 자체가 없거나(dispatcher INSERT 실패 후 호출), 누군가 SUCCESS/FAILED/RUNNING 으로 옮긴 케이스.
            //     이는 dispatcher / 워커 / sweeper 어느 한 쪽의 정합성 버그 신호이므로 escalate.
            throw new InvalidOperationException(
                $"[{logId}] MarkRunning failed — row not in ENQUEUED/DEPRECATED (state inconsistent)");
        });

    /// <summary>
    /// RUNNING → SUCCESS 로 전이 시도. RUNNING 이 아니면(이미 DEPRECATED 등) 0 rows → false. 호출자는
    /// "비즈니스는 성공했지만 row 는 다른 상태였다" 로 해석하고 warning 로그만 남기면 된다.
    /// </summary>
    public bool TryMarkSuccess(string logId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            return ctx.JobLogs
                .Where(r => r.LogId == logId && r.Status == JobStatus.RUNNING)
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,     JobStatus.SUCCESS)
                    .SetProperty(r => r.FinishedDt, DatetimeHelper.Now)) == 1;
        });

    /// <summary>
    /// RUNNING → SUCCESS 로 전이하면서 ERROR_MESSAGE 에 "Skipped: test mode" 마커를 남긴다.
    /// <see cref="JobArgs.IsTest"/>=true 인 잡에 대해 <see cref="Jobs.JobBase{TSelf,TParam}"/> 가
    /// ExecuteCoreAsync 를 우회한 직후 호출. STATUS 는 SUCCESS 로 통일해 기존 sweeper/대시보드 정책을
    /// 깨지 않고 TEST_YN='Y' + ERROR_MESSAGE 마커 조합으로 사후 통계에서 운영 row 와 분리한다.
    /// </summary>
    public bool TryMarkSuccessAsTestSkipped(string logId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            return ctx.JobLogs
                .Where(r => r.LogId == logId && r.Status == JobStatus.RUNNING)
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,       JobStatus.SUCCESS)
                    .SetProperty(r => r.FinishedDt,   DatetimeHelper.Now)
                    .SetProperty(r => r.ErrorMessage, "Skipped: test mode")) == 1;
        });

    /// <summary>
    /// ENQUEUED/RUNNING → FAILED 로 전이 시도. <paramref name="ex"/> 의 type + message 를 우선 보존한 뒤
    /// stack 요약을 남는 자리에 채워 ERROR_MESSAGE 컬럼(2000) 한도 안에서 truncate (잘렸을 때 마커 포함).
    ///
    /// <para>
    /// from-status 가 ENQUEUED/RUNNING 둘 다 인 이유: <see cref="Jobs.JobBase{TSelf,TParam}"/> 가 단일 try/catch
    /// 로 통합되면서, RUNNING 전이 이전 단계(페이로드 역직렬화·검증·<see cref="MarkRunningOrThrow"/> 자체)에서
    /// 깨진 row 도 ENQUEUED 상태인 채 catch 에 들어온다. 종료 상태(SUCCESS/FAILED/DEPRECATED/SKIPPED_DUP) 는
    /// 여전히 가드로 막혀 sweeper / 다른 워커가 이미 마감한 row 를 덮어쓰지 않는다 — 그 케이스는 0 rows
    /// 반환되어 호출자의 warning 로그만 남는다.
    /// </para>
    /// </summary>
    public bool TryMarkFailed(string logId, Exception ex)
    {
        var formatted = FormatErrorMessage(ex);
        return _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            return ctx.JobLogs
                .Where(r => r.LogId == logId
                         && (r.Status == JobStatus.ENQUEUED || r.Status == JobStatus.RUNNING))
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,       JobStatus.FAILED)
                    .SetProperty(r => r.FinishedDt,   DatetimeHelper.Now)
                    .SetProperty(r => r.ErrorMessage, formatted)) == 1;
        });
    }

    public void MarkDeprecated(string logId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            ctx.JobLogs
                .Where(r => r.LogId == logId)
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,     JobStatus.DEPRECATED)
                    .SetProperty(r => r.FinishedDt, DatetimeHelper.Now));
        });

    /// <summary>
    /// Admin UI 의 수동 DEPRECATED 흐름 1단계 — row 존재 / 현재 status 만 검증해서 컨트롤러에 넘긴다.
    /// 실제 UPDATE 는 컨트롤러가 Hangfire 상태를 보고 분기 결정한 뒤 <see cref="TransitionFromActiveGuarded"/>
    /// 로 따로 호출. repo 를 Hangfire 의존에서 떼어내려고 두 단계로 쪼갠 형태.
    ///
    /// <list type="bullet">
    /// <item><description>row 없음 → <see cref="NotFoundException"/> (404).</description></item>
    /// <item><description>현재 STATUS 가 ENQUEUED/RUNNING 이 아님 → <see cref="BusinessRuleException"/> (409). 이미 종료된 잡은 수동으로 손댈 의미가 없다.</description></item>
    /// </list>
    /// </summary>
    public JobLogForDeprecate ValidateForDeprecate(string logId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.JobLogs.AsNoTracking()
                .Where(r => r.LogId == logId)
                .Select(r => new { r.Status, r.HangfireJobId })
                .FirstOrDefault()
                ?? throw new NotFoundException("JobLog", logId);

            if (row.Status != JobStatus.ENQUEUED && row.Status != JobStatus.RUNNING)
                throw new BusinessRuleException(
                    $"Only ENQUEUED/RUNNING jobs can be deprecated manually (current: {row.Status}).",
                    StatusCodes.Status409Conflict);

            return new JobLogForDeprecate(row.Status, row.HangfireJobId);
        });

    /// <summary>
    /// ENQUEUED/RUNNING → 임의의 종료 status 로 from-status 가드 전이. 수동 DEPRECATED 경로
    /// (<paramref name="targetStatus"/>=DEPRECATED) 와 Hangfire 가 이미 종료한 잡을 그 결과로 sync
    /// 하는 정합성 복구 경로(SUCCESS/FAILED/DEPRECATED) 가 공유한다. 검증은 호출자가 미리
    /// <see cref="ValidateForDeprecate"/> 로 끝냈다고 가정하며, SELECT~UPDATE 사이의 race-loss 는
    /// 가드 덕에 0 rows 로 흘러간다.
    ///
    /// <para><paramref name="errorMessage"/> 는 그대로 SET — null 이면 ERROR_MESSAGE 가 null 로
    /// 덮어쓰이지만, ENQUEUED/RUNNING row 는 원래 ERROR_MESSAGE 가 비어있는 게 정상이라 손실 없음.</para>
    /// </summary>
    public bool TransitionFromActiveGuarded(string logId, JobStatus targetStatus, string? errorMessage) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            return ctx.JobLogs
                .Where(r => r.LogId == logId
                         && (r.Status == JobStatus.ENQUEUED || r.Status == JobStatus.RUNNING))
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,       targetStatus)
                    .SetProperty(r => r.FinishedDt,   DatetimeHelper.Now)
                    .SetProperty(r => r.ErrorMessage, errorMessage)) == 1;
        });

    /// <summary>
    /// 여러 LogId 를 한 statement 로 DEPRECATED 전이. <see cref="Jobs.DeprecationSweeper"/> 가
    /// row-by-row 루프 대신 호출해 NC index 페이지 깨움 횟수와 락 보유 시간을 줄인다 — Dispatcher
    /// SELECT 와의 deadlock 빈도를 낮추기 위함. <paramref name="fromStatus"/> 외의 status 로
    /// 이미 전이된 race-loser (ENQUEUED→RUNNING, RUNNING→SUCCESS/FAILED 등) 는 filter 로 보호.
    /// </summary>
    public int MarkManyDeprecated(IReadOnlyCollection<string> logIds, JobStatus fromStatus)
    {
        if (logIds.Count == 0) return 0;

        return _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            var now = DatetimeHelper.Now;
            return ctx.JobLogs
                .Where(r => logIds.Contains(r.LogId) && r.Status == fromStatus)
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.Status,     JobStatus.DEPRECATED)
                    .SetProperty(r => r.FinishedDt, now));
        });
    }

    public void LogSkippedDup(string logId) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            ctx.JobLogs
                .Where(r => r.LogId == logId && r.Status == JobStatus.ENQUEUED)
                .ExecuteUpdate(s => s.SetProperty(r => r.Status, JobStatus.SKIPPED_DUP));
        });

    /// <summary>
    /// Admin UI — filtered + paged query. Orders by SCHEDULED_UTC DESC, ENQUEUED_UTC DESC.
    /// 2차 키 ENQUEUED_UTC 는 분 단위 bucketing 된 SCHEDULED_UTC 동률 안에서 실제 enqueue 순서로
    /// 갈라줘 같은 분 안에 섞인 이전 테스트 row 가 운영 row 위로 떠오르는 비결정적 순서를 막는다.
    /// <c>WITH (NOLOCK)</c> 힌트로 Dispatcher INSERT / 상태 전이 UPDATE 와의 shared/X
    /// lock 경합을 제거 — dirty read 가능성을 받아들이는 대신 Admin 화면 응답이
    /// sweep / fan-out 분 경계에서도 안 끊김.
    /// </summary>
    public PagedResult<JobLogDto> QueryLogs(JobLogQuery q)
    {
        using var ctx = _factory.CreateDbContext();

        var page = q.Page < 1 ? 1 : q.Page;
        var size = q.Size switch { < 1 => 50, > 500 => 500, _ => q.Size };

        // FromSqlRaw 로 NOLOCK 힌트 박힌 base 를 만들고 위에 LINQ composable.
        // 컴파일된 SQL 은 SELECT ... FROM (SELECT * FROM SCH_JOB_LOG WITH (NOLOCK)) AS t WHERE ...
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
        // TestYn 은 'Y'|'N'|null 3-state. null 이면 필터 없음(전체). 'N' 이 운영자가 가장 자주 보는 default.
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
    /// (DomainCode, BrandCode) 의 활성(ENQUEUED/RUNNING) Job 의 LogId/Status 반환.
    /// 활성 Job 없으면 null. Dispatcher 의 cron(<c>Fire</c>) 및 manual(<c>EnqueueOne</c>/<c>EnqueueDomain</c>)
    /// 진입점 모두 호출 — cron 은 SKIPPED_DUP row INSERT 시 메시지에, manual 은 응답에 점유 LogId 를 노출.
    /// 동일 페어의 활성이 여러 개면 가장 최근 ENQUEUED 를 반환 (정상 흐름에서는 1개).
    /// stale RUNNING 정리는 <c>DeprecationSweeper</c> 책임으로 분리.
    /// </summary>
    public (string LogId, JobStatus Status)? GetActiveJob(string domainCode, string brandCode) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.JobLogs
                .AsNoTracking()
                .Where(r => r.DomainCode == domainCode
                         && r.BrandCode  == brandCode
                         && (r.Status == JobStatus.ENQUEUED || r.Status == JobStatus.RUNNING))
                .OrderByDescending(r => r.EnqueuedDt)
                .Select(r => new { r.LogId, r.Status })
                .FirstOrDefault();
            return row == null ? ((string, JobStatus)?)null : (row.LogId, row.Status);
        });

    /// <summary>
    /// 한 도메인의 모든 브랜드 활성잡을 한 round-trip 으로 조회. cron fan-out 의 브랜드별
    /// <see cref="GetActiveJob"/> N회 호출을 1회로 압축해 SCH_JOB_LOG SELECT 부하/락 표면을
    /// 줄인다 — 분 경계에 도메인 12개가 동시 fire 할 때 누적되던 deadlock 빈도를 낮추기 위함.
    /// 같은 (Domain, Brand) 에 활성 row 가 여러 개면 가장 최근 ENQUEUED 가 채택된다
    /// (단일 호출 시멘틱과 동일).
    /// </summary>
    public ActiveJobMap GetActiveJobsByBrand(string domainCode) =>
        _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();
            var rows = ctx.JobLogs
                .AsNoTracking()
                .Where(r => r.DomainCode == domainCode
                         && (r.Status == JobStatus.ENQUEUED || r.Status == JobStatus.RUNNING))
                .OrderByDescending(r => r.EnqueuedDt)
                .Select(r => new ActiveJobRow(r.BrandCode, r.LogId, r.Status))
                .ToList();

            return new ActiveJobMap(rows);
        });

    /// <summary>
    /// cron fan-out 시 동일 (Domain, Brand) 의 활성 Job 때문에 enqueue 를 건너뛴 사실을
    /// SCH_JOB_LOG 에 기록. STATUS=SKIPPED_DUP, ERROR_MESSAGE 에 어떤 LogId 가 활성 중인지 명시.
    /// LogId UNIQUE 충돌(같은 분 두 번째 fire 등) 시 false 반환 — 호출자는 무시.
    /// </summary>
    public bool InsertSkippedDup(JobArgs args, string activeLogId, JobStatus activeStatus)
    {
        return _retry.Execute(() =>
        {
            using var ctx = _factory.CreateDbContext();

            if (ctx.JobLogs.Any(r => r.LogId == args.LogId)) return false;

            var now = DatetimeHelper.Now;
            ctx.JobLogs.Add(new SchJobLog
            {
                LogId        = args.LogId,
                DomainCode   = args.DomainCode,
                BrandCode    = args.BrandCode,
                CenterCode   = args.CenterCode,
                ScheduledDt  = args.BucketedDt,
                EnqueuedDt   = now,
                FinishedDt   = now,
                Status       = JobStatus.SKIPPED_DUP,
                TimeoutSec   = args.TimeoutSec,
                DeprecateSec = args.DeprecateSec,
                ErrorMessage = $"Active job exists: LogId={activeLogId} Status={activeStatus}",
                TestYn       = args.IsTest ? "Y" : "N",
                Requester    = args.Requester
            });

            try
            {
                ctx.SaveChanges();
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        });
    }

    public IReadOnlyList<StaleJobRow> FindStaleEnqueuedJobs()
    {
        using var ctx = _factory.CreateDbContext();
        var now = DatetimeHelper.Now;

        return ctx.JobLogs
            .AsNoTracking()
            .Where(r => r.Status == JobStatus.ENQUEUED)
            // DEPRECATE_SEC(큐 대기 한도) 기준 — TIMEOUT_SEC(시작 후 실행 한도) 이 아니다.
            // 0 = 무제한이므로 가드가 선행해야 한다 (없으면 전량 즉시 폐기).
            //
            // 기준 시각은 SCHEDULED_DT(= JobArgs.BucketedDt, 잡이 돌기로 "예정됐던" 시각) 이며
            // DeprecationFilter 와 동일하다. ENQUEUED_DT(row INSERT 시각) 를 쓰면 Dispatcher 가
            // 밀린 만큼 나이가 리셋돼, 예정 시각 기준으로는 이미 무의미해진 잡이 더 오래 살아남는다.
            .Where(r => r.DeprecateSec > 0
                     && EF.Functions.DateDiffSecond(r.ScheduledDt, now) > r.DeprecateSec)
            .Select(StaleProjection)
            .ToList();
    }

    /// <summary>
    /// RUNNING 상태에서 <c>STARTED_UTC + TIMEOUT_SEC + graceSeconds</c> 를 초과한 잡을 반환.
    /// 워커가 ExecuteAsync 안에서 멎거나(데드락·무한 대기·프로세스 강제 종료) Hangfire 가 결과
    /// 콜백을 못 써 SUCCESS/FAILED 전이를 못 한 케이스를 마감한다. grace 는 정상 종료 직전 row
    /// 가 race-loser 로 DEPRECATED 되지 않도록 둔 환경 변동(GC pause, UPDATE 지연) 여유분.
    /// </summary>
    public IReadOnlyList<StaleJobRow> FindStaleRunningJobs(int graceSeconds)
    {
        using var ctx = _factory.CreateDbContext();
        var now = DatetimeHelper.Now;

        return ctx.JobLogs
            .AsNoTracking()
            .Where(r => r.Status == JobStatus.RUNNING && r.StartedDt != null)
            .Where(r => EF.Functions.DateDiffSecond(r.StartedDt!.Value, now) > r.TimeoutSec + graceSeconds)
            .Select(StaleProjection)
            .ToList();
    }

    /// <summary>
    /// 예외 정보를 ERROR_MESSAGE 컬럼(2000) 에 안전하게 직렬화. type + message 가 우선이고 stack 요약은
    /// 남는 자리에만 채운다 — <c>ex.ToString()</c> 의 첫 2000자가 stack frame 으로 채워져 정작
    /// message 가 잘려나가는 사고를 막기 위함. 잘렸으면 <c> ... (truncated)</c> 로 표시해 운영자가
    /// 잘림 자체를 인지할 수 있게 한다.
    /// </summary>
    private static string FormatErrorMessage(Exception ex)
    {
        var head = $"[{ex.GetType().Name}] {ex.Message}";
        var stack = ex.StackTrace ?? string.Empty;
        var combined = string.IsNullOrEmpty(stack) ? head : $"{head}\n{stack}";

        if (combined.Length <= ErrorMessageMaxLen) return combined;

        var keep = ErrorMessageMaxLen - TruncationSuffix.Length;
        return combined[..keep] + TruncationSuffix;
    }
}

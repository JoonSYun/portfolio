// [담당업무 2] 발사·필터·적재 단계 — 도메인 cron tick 하나를 브랜드별 JobArgs 로 fan-out 해 Hangfire 큐에 enqueue 한다.

using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// Scheduler-Dispatcher-Worker 3-tier 의 중간 계층. 도메인 단위 cron tick 한 번을 받아
/// 브랜드별 <see cref="JobArgs"/> 를 조립하고 Hangfire 큐에 enqueue 한다.
/// </summary>
/// <remarks>
/// <para>
/// 진입점은 3개이며 모두 <see cref="EnqueueTarget"/> 를 거쳐 동일한 Hangfire enqueue 경로를 공유한다.
/// </para>
/// <list type="table">
///   <listheader><term>진입점</term><description>호출 주체와 시멘틱</description></listheader>
///   <item><term><see cref="Fire"/></term>
///         <description>Hangfire RecurringJob 자동 발사. USE_YN/그룹/날짜/cron override 필터 모두 적용</description></item>
///   <item><term><see cref="EnqueueOne"/></term>
///         <description>Admin "Run now" — 단일 (Domain, Brand). SCH_DOMAIN.USE_YN 만 강제, 나머지 활성 필터 우회</description></item>
///   <item><term><see cref="EnqueueDomain"/></term>
///         <description>Admin "Run domain now" — 도메인 fan-out. SCH_DOMAIN.USE_YN 만 강제, 나머지 활성 필터 우회</description></item>
/// </list>
/// </remarks>
public class Dispatcher
{
    /// <summary>
    /// cron 자동 발사(<see cref="Fire"/>) 경로의 <see cref="JobArgs.Requester"/> 값.
    /// 수동 진입점은 RunNowController 가 JWT NameIdentifier claim 을 넘겨준다.
    /// </summary>
    public const string SchedulerRequester = "Scheduler";

    private readonly ScheduleRepository   _repo;
    private readonly IBackgroundJobClient _bg;
    private readonly DomainJobRegistry    _registry;
    private readonly JobLogRepository     _logs;
    private readonly DispatchPauseStore   _pauseStore;
    private readonly ILogger<Dispatcher>  _logger;

    public Dispatcher(
        ScheduleRepository   repo,
        IBackgroundJobClient bg,
        DomainJobRegistry    registry,
        JobLogRepository     logs,
        DispatchPauseStore   pauseStore,
        ILogger<Dispatcher>  logger)
    {
        _repo       = repo;
        _bg         = bg;
        _registry   = registry;
        _logs       = logs;
        _pauseStore = pauseStore;
        _logger     = logger;
    }

    /// <summary>
    /// Hangfire RecurringJob 의 자동 발사 진입점. 도메인 한 tick 을 받아 활성 브랜드 fan-out 으로 enqueue 한다.
    /// </summary>
    /// <param name="domainCode">SCH_DOMAIN.DOMAIN_CODE — RecurringJob 식별자와 동일한 키.</param>
    /// <param name="scheduledUtc">
    /// Hangfire 가 채워주는 예정 시각. 실제로는 Bootstrapper 가 항상 null 로 호출하고
    /// (<see cref="Bootstrapper.RecurringJobRegistrar"/>), null 일 때 <see cref="DatetimeHelper.Now"/> 가 fallback.
    /// 파라미터는 테스트/수동 시뮬레이션용 hook 으로만 의미가 있다.
    /// </param>
    /// <exception cref="Exception">조회/INSERT 단계에서 발생한 예외는 그대로 재던진다 — Hangfire 가 retry/실패 처리.</exception>
    public void Fire(string domainCode, DateTime? scheduledUtc = null)
    {
        var firedAt     = scheduledUtc ?? DatetimeHelper.Now;
        var bucketedDt = TruncateToMinute(firedAt);

        // [전역 일시정지 가드] DispatchPauseStore.Current 는 메모리 캐시 — DB / 파일 I/O 미발생.
        // 본 가드는 SCH_JOB_LOG INSERT 가 일어나는 IServerFilter 보다 앞단에 위치하므로
        // pause 중에는 sch_job_log 에 어떤 행(ENQUEUED / SKIPPED_DUP 포함)도 남지 않는다.
        // 수동 실행 경로(EnqueueOne / EnqueueDomain)는 본 가드를 통과하지 않아 점검 중에도 관리자가 강제 실행 가능.
        var pause = _pauseStore.Current;
        if (pause.IsPaused)
        {
            _logger.LogDispatchContext(domainCode, bucketedDt,
                $"Dispatch skipped — globally paused (By={pause.PausedBy}, At={pause.PausedDt:yyyy-MM-dd HH:mm}, Reason={pause.Reason})");
            return;
        }

        _logger.LogDispatchContext(domainCode, bucketedDt, "Dispatch started");

        var enqueued = 0;
        var skipped  = 0;

        try
        {
            // SCH_DOMAIN_BRAND_OVERRIDE × SCH_DOMAIN_SCHEDULE × SCH_DOMAIN × SCH_BRAND 4-way JOIN.
            //   필터:
            //     - SCH_DOMAIN.USE_YN='Y'
            //     - SCH_DOMAIN_GROUP 매핑된 그룹 중 USE_YN='N' 가 0개 (NOT EXISTS, V008 시멘틱)
            //     - SCH_DOMAIN_SCHEDULE.RUN_AUTO_YN='Y'
            //     - SCH_DOMAIN_BRAND_OVERRIDE.USE_YN='Y'
            //     - SCH_BRAND.USE_YN='Y'
            //     - VALID_FROM/VALID_TO 범위 (스케줄·오버라이드 양쪽, KST 기준 today)
            //   결과 컬럼:
            //     L1(SCH_DOMAIN_SCHEDULE) 기본값을 L2(SCH_DOMAIN_BRAND_OVERRIDE) 가 있으면 덮어쓴다
            //     (PayloadJson, TimeoutSec, TimezoneId, Queue 등).
            IReadOnlyList<TargetRow> targets = _repo.GetActiveBrandTargets(domainCode, bucketedDt);

            // SCH_JOB_LOG 1회 SELECT 로 도메인 내 ENQUEUED/RUNNING row 를 BrandCode → (LogId, Status)
            // dictionary 로 적재. 브랜드별 GetActiveJob round-trip 을 N → 1 로 압축해 분 경계 동시 fire 시
            // SCH_JOB_LOG SELECT 부하/락 표면을 줄이고 워커 UPDATE 와의 deadlock 빈도를 낮추기 위함.
            ActiveJobMap actives = _logs.GetActiveJobsByBrand(domainCode);

            foreach (var t in targets)
            {
                // 브랜드 시간대 cron 이 있으면 이번 bucket 에 발사할 분인지 추가 검사 (narrowing).
                // cron 0개면 도메인 L1 상속(항상 발사), 1개 이상이면 그 중 하나라도 매치할 때만 발사(OR).
                if (!IsAnyFireTime(t.CronExprs, bucketedDt))
                {
                    skipped++;
                    continue;
                }

                // 같은 (DomainCode, BrandCode) 의 활성 잡(ENQUEUED/RUNNING) 이 있으면 enqueue 우회.
                // SCH_JOB_LOG 에 STATUS=SKIPPED_DUP, ERROR_MESSAGE="Active job exists: LogId=... Status=..."
                // row 를 INSERT — skip 된 fire 도 감사 추적에 남긴다.
                if (actives.TryGet(t.BrandCode, out var active))
                {
                    var skipArgs = new JobArgs
                    {
                        DomainCode  = t.DomainCode,
                        BrandCode   = t.BrandCode,
                        CenterCode  = t.CenterCode,
                        PayloadJson = t.PayloadJson,
                        LogId       = LogIdFactory.New(),
                        BucketedDt = bucketedDt,
                        TimeoutSec  = t.TimeoutSec,
                        DeprecateSec = t.DeprecateSec,
                        IsTest      = t.IsTest,
                        Requester   = SchedulerRequester
                    };
                    _logs.InsertSkippedDup(skipArgs, active.LogId, active.Status);
                    _logger.LogInformation(AppLog.Log(
                        "[{DomainCode}] Skip enqueue (active job exists) Brand={BrandCode} ActiveLogId={ActiveLogId} ActiveStatus={ActiveStatus} Bucket={BucketedDt:yyyy-MM-dd HH:mm}"),
                        t.DomainCode, t.BrandCode, active.LogId, active.Status, bucketedDt);
                    skipped++;
                    continue;
                }

                EnqueueTarget(t, bucketedDt, isManual: false, runAs: null, requester: SchedulerRequester);
                enqueued++;
            }

            _logger.LogDispatchContext(domainCode, bucketedDt,
                $"Dispatch completed Enqueued={enqueued} Skipped={skipped} Total={enqueued + skipped}");
        }
        catch (Exception ex)
        {
            _logger.ErrorLogDispatchContext(domainCode, bucketedDt, ex, "Dispatch failed");
            throw;
        }
    }

    /// <summary>
    /// Admin "Run now" 진입점. 단일 (DomainCode, BrandCode) 페어를 SCH_DOMAIN.USE_YN 만 강제하고
    /// 나머지 활성 필터(그룹/스케줄 RUN_AUTO_YN/override·brand USE_YN/날짜)는 우회한 채 즉시 enqueue 한다.
    /// </summary>
    /// <param name="domainCode">SCH_DOMAIN.DOMAIN_CODE.</param>
    /// <param name="brandCode">SCH_BRAND.BRAND_CODE.</param>
    /// <returns>
    /// 정상 enqueue 시 <see cref="ManualRunResult.LogId"/>/<see cref="ManualRunResult.HangfireJobId"/>/
    /// <see cref="ManualRunResult.Queue"/> 가 채워진 결과. 활성 잡이 이미 있으면 그 3개는 null 이고
    /// <see cref="ManualRunResult.SkipReason"/> + <see cref="ManualRunResult.ActiveLogId"/>/
    /// <see cref="ManualRunResult.ActiveStatus"/> 가 채워진다 (throw 하지 않음).
    /// </returns>
    /// <exception cref="NotFoundException">SCH_DOMAIN_BRAND_OVERRIDE 행 자체가 없거나 도메인이 삭제된 경우.</exception>
    /// <exception cref="InvalidOperationException">JOB_TYPE 이 <see cref="DomainJobRegistry"/> 에 미등록.</exception>
    /// <exception cref="BusinessRuleException">
    /// SCH_DOMAIN.USE_YN='N' (도메인 자체 비활성) 또는 DB 측 IsTest=Y 인데 <paramref name="runAs"/>=Operational
    /// 로 호출된 경우 모두 400.
    /// </exception>
    public ManualRunResult EnqueueOne(string domainCode, string brandCode, RunAsMode runAs, string requester)
    {
        // [SCH_DOMAIN.USE_YN 가드] 도메인 자체가 비활성이면 즉시 거절. override / schedule / brand 의 USE_YN 은
        // cron 자동 발사만 막는 의미이므로 manual run 에서는 무시하되, SCH_DOMAIN.USE_YN='N' 은 도메인 자체
        // 비활성이라 manual 도 막는다. 도메인 row 자체가 없는 경우는 아래 GetTargetForManualRun 의 null →
        // NotFound("Override") 가 잡으므로 여기서는 별도 처리하지 않음.
        if (_repo.GetDomainUseYn(domainCode) == "N")
            throw new BusinessRuleException(
                $"Cannot run inactive domain: {domainCode} (SCH_DOMAIN.USE_YN='N').",
                StatusCodes.Status400BadRequest);

        // SCH_DOMAIN_BRAND_OVERRIDE × SCH_DOMAIN_SCHEDULE × SCH_DOMAIN × SCH_BRAND 4-way JOIN으로 단일 페어 조회.
        //   필터: WHERE OVERRIDE.DOMAIN_CODE=@d AND OVERRIDE.BRAND_CODE=@b 만 적용 — 그룹/RUN_AUTO_YN/
        //         VALID_FROM/VALID_TO 및 override/brand 의 USE_YN 우회. 위 가드로 도메인 USE_YN 만 강제.
        var target = _repo.GetTargetForManualRun(domainCode, brandCode)
                     ?? throw new NotFoundException("Override", $"{domainCode}/{brandCode}");

        // DB 측 IsTest=Y 인데 사용자가 운영 실행을 시도한 케이스 — UI 가 잘못된 선택지를 보낸 것.
        // 강제 강등은 위험하므로 명시적으로 거절. 정상 흐름이라면 프론트의 RunNowModal 이 운영 옵션을 비활성화한다.
        if (target.IsTest && runAs == RunAsMode.Operational)
            throw new BusinessRuleException(
                $"Cannot run {domainCode}/{brandCode} as operational — DB-side IsTest=Y (forced test).",
                StatusCodes.Status400BadRequest);

        // SCH_JOB_LOG WHERE DOMAIN_CODE=@d AND BRAND_CODE=@b AND STATUS IN (ENQUEUED,RUNNING)
        //   ORDER BY ENQUEUED_UTC DESC LIMIT 1.
        var active = _logs.GetActiveJob(target.DomainCode, target.BrandCode);
        if (active.HasValue)
        {
            _logger.LogInformation(AppLog.Log(
                "[{DomainCode}] Skip manual enqueue (active job exists) Brand={BrandCode} ActiveLogId={ActiveLogId} ActiveStatus={ActiveStatus}"),
                target.DomainCode, target.BrandCode, active.Value.LogId, active.Value.Status);
            return new ManualRunResult(
                target.DomainCode, target.BrandCode, null, null, null,
                $"이미 실행 중 (LogId={active.Value.LogId} Status={active.Value.Status})",
                IsTest:       target.IsTest || runAs == RunAsMode.Test,
                ActiveLogId:  active.Value.LogId,
                ActiveStatus: active.Value.Status.ToString());
        }

        // cron 흐름의 BucketedDt 와 의미를 일치시키기 위해 분 단위 truncate.
        var bucketedDt = TruncateToMinute(DatetimeHelper.Now);
        return EnqueueTarget(target, bucketedDt, isManual: true, runAs: runAs, requester: requester);
    }

    /// <summary>
    /// Admin "Run domain now" 진입점. 도메인에 매핑된 모든 브랜드 오버라이드를 한 번에 fan-out enqueue 한다.
    /// 부분 성공 허용 — 점유된 브랜드만 skip 하고 나머지는 enqueue.
    /// </summary>
    /// <param name="domainCode">SCH_DOMAIN.DOMAIN_CODE.</param>
    /// <returns>
    /// 매핑된 브랜드 수만큼의 결과. 활성 잡이 있는 브랜드는 <see cref="ManualRunResult.SkipReason"/> 채워진
    /// 결과로 포함된다. 매핑 0개면 빈 리스트.
    /// </returns>
    /// <exception cref="NotFoundException">SCH_DOMAIN row 자체가 부재. 매핑 0개와 도메인 부재를 구분하기 위함.</exception>
    /// <exception cref="InvalidOperationException">JOB_TYPE 이 <see cref="DomainJobRegistry"/> 에 미등록.</exception>
    /// <exception cref="BusinessRuleException">SCH_DOMAIN.USE_YN='N' — 도메인 자체가 비활성 (400).</exception>
    /// <remarks>
    /// <paramref name="runAs"/> 는 fan-out 전체에 동일 적용. 단, 그 중 DB 측 IsTest=Y 인 row 는 strict 하게 강제 test —
    /// runAs=Operational 이면 그 row 들만 골라 400 으로 끊지 않고 자동으로 test 로 흘린다(부분 성공 정책 유지).
    /// 단일 단위 EnqueueOne 과의 동작 차이는 fan-out 의 "전체 거부 vs 일부 우회" 트레이드오프 — 운영자가 그룹/도메인 단위로
    /// 일괄 실행할 때 한 브랜드 때문에 전체가 막히면 디버깅이 어려워지므로 fan-out 쪽은 관대하게 둠.
    /// </remarks>
    public IReadOnlyList<ManualRunResult> EnqueueDomain(string domainCode, RunAsMode runAs, string requester)
    {
        // [SCH_DOMAIN.USE_YN 가드] 한 번의 조회로 "도메인 부재"(null) / "비활성"(N) / "정상"(Y) 를 모두 분기.
        // 도메인 비활성은 fan-out 전체를 막는다 (관대한 부분 성공 정책의 예외) — 도메인 자체가 비활성인 상황에서
        // 매핑된 브랜드만 골라 돌리는 케이스는 의미가 없으므로 EnqueueOne 과 동일한 strict 정책 적용.
        var useYn = _repo.GetDomainUseYn(domainCode);
        if (useYn == null)
            throw new NotFoundException("Domain", domainCode);
        if (useYn == "N")
            throw new BusinessRuleException(
                $"Cannot run inactive domain: {domainCode} (SCH_DOMAIN.USE_YN='N').",
                StatusCodes.Status400BadRequest);

        // SCH_DOMAIN_BRAND_OVERRIDE × SCH_DOMAIN_SCHEDULE × SCH_DOMAIN × SCH_BRAND 4-way JOIN.
        //   필터: WHERE OVERRIDE.DOMAIN_CODE=@d ORDER BY OVERRIDE.BRAND_CODE — 그룹/RUN_AUTO_YN/
        //         VALID_FROM/VALID_TO 및 override/brand 의 USE_YN 우회. 도메인 USE_YN 은 위 가드로 강제.
        var targets = _repo.GetTargetsForManualDomainRun(domainCode);

        // cron 흐름과 동일한 BucketedDt 를 모든 fan-out row 에 공유 — 동일 manual click 을 한 그룹으로 묶기 위함.
        var bucketedDt = TruncateToMinute(DatetimeHelper.Now);

        // SCH_JOB_LOG 1회 SELECT — Fire 의 fan-out 과 동일하게 N 브랜드 GetActiveJob 을 1회로 압축.
        var actives = _logs.GetActiveJobsByBrand(domainCode);

        var results = new List<ManualRunResult>(targets.Count);
        foreach (var t in targets)
        {
            // 점유된 브랜드는 SkipReason 채운 결과로 응답 — 부분 성공 허용 (다른 브랜드 enqueue 막지 않음).
            if (actives.TryGet(t.BrandCode, out var active))
            {
                _logger.LogInformation(AppLog.Log(
                    "[{DomainCode}] Skip manual enqueue (active job exists) Brand={BrandCode} ActiveLogId={ActiveLogId} ActiveStatus={ActiveStatus}"),
                    t.DomainCode, t.BrandCode, active.LogId, active.Status);
                results.Add(new ManualRunResult(
                    t.DomainCode, t.BrandCode, null, null, null,
                    $"이미 실행 중 (LogId={active.LogId} Status={active.Status})",
                    IsTest:       t.IsTest || runAs == RunAsMode.Test,
                    ActiveLogId:  active.LogId,
                    ActiveStatus: active.Status.ToString()));
                continue;
            }
            results.Add(EnqueueTarget(t, bucketedDt, isManual: true, runAs: runAs, requester: requester));
        }
        return results;
    }

    /// <summary>
    /// 세 진입점이 공유하는 enqueue 본문. <see cref="JobArgs"/> 조립 → JOB_TYPE 문자열 → <see cref="Type"/> resolve →
    /// <see cref="IBackgroundJobClient.Create"/> 호출. SCH_JOB_LOG INSERT 는 <see cref="JobBase"/> 의
    /// IServerFilter 가 워커 진입 시 처리하므로 여기서는 하지 않는다.
    /// </summary>
    /// <param name="t">활성 필터를 통과한 타겟 row (Repository 조회 결과).</param>
    /// <param name="bucketedDt">cron tick (또는 manual 의 truncate(Now)) — 분 단위 정렬된 KST 시각.</param>
    /// <param name="isManual">로깅 메시지 분기 ("Enqueued" vs "Enqueued (manual)") 용 플래그.</param>
    /// <param name="runAs">
    /// 수동 실행 경로의 mode (null = cron 자동 발사). 실효 IsTest = <c>t.IsTest || (runAs == Test)</c>.
    /// cron 자동 발사는 항상 <c>t.IsTest</c> 만 본다 (사용자 의도 없음).
    /// </param>
    /// <param name="requester">
    /// SCH_JOB_LOG.REQUESTER 에 그대로 저장될 발화자 식별자. cron 경로는 <see cref="SchedulerRequester"/>,
    /// 수동 경로는 RunNowController 가 JWT NameIdentifier claim 으로 채운 로그인 아이디.
    /// </param>
    /// <returns>HangfireJobId / LogId / Queue / IsTest 가 채워진 정상 결과 (SkipReason 항상 null).</returns>
    /// <exception cref="InvalidOperationException">JOB_TYPE → Type 매핑 실패 또는 Type 이 IDomainJob.ExecuteAsync 미구현.</exception>
    private ManualRunResult EnqueueTarget(TargetRow t, DateTime bucketedDt, bool isManual, RunAsMode? runAs, string requester)
    {
        // 호출마다 unique 한 LogId 발급. cron/manual 구분 prefix 가 없고 중복 enqueue 방지는
        // (DomainCode, BrandCode) 활성잡 검사가 담당하므로 LogId 형식에 의존하지 않는다.
        var logId = LogIdFactory.New();

        // 실효 IsTest: DB 가 Y 면 무조건 test (mode 무시). DB 가 N 일 때만 사용자 선택 반영.
        // cron 자동 발사는 runAs=null → DB 값 그대로.
        var effectiveIsTest = t.IsTest || runAs == RunAsMode.Test;

        var jobArgs = new JobArgs
        {
            DomainCode  = t.DomainCode,
            BrandCode   = t.BrandCode,
            CenterCode  = t.CenterCode,
            PayloadJson = t.PayloadJson,
            LogId       = logId,
            BucketedDt = bucketedDt,
            TimeoutSec  = t.TimeoutSec,
            DeprecateSec = t.DeprecateSec,
            IsTest      = effectiveIsTest,
            SystemMailSend = t.SystemMailSend,
            BrandMailSend  = t.BrandMailSend,
            Requester   = requester
        };

        // SCH_DOMAIN.JOB_TYPE 은 short class name (예: "DBReindexJob") 로 저장 → DomainJobRegistry 가
        // assembly scan 결과 dictionary 에서 Type 으로 변환. DB row 가독성과 namespace 변경 분리를 동시 달성.
        var jobType = _registry.Resolve(t.JobType);
        var method  = jobType.GetMethod(nameof(IDomainJob.ExecuteAsync))
                      ?? throw new InvalidOperationException(
                          $"{t.JobType} must implement IDomainJob.ExecuteAsync");

        // CancellationToken.None 은 enqueue 직렬화용 placeholder 일 뿐 — Hangfire 1.8 이 worker 시점에
        // ServerJobCancellationToken (shutdown / job-delete 신호 합본) 으로 자동 교체한다. 직렬화 라운드트립
        // 후에도 빈 토큰 그대로 들어가지 않으므로 Sweeper 가 BackgroundJob.Delete 한 잡은 즉시 cancel 된다.
        var job = new Job(jobType, method, new object[] { jobArgs, CancellationToken.None });

        // EnqueuedState(큐이름) 가 HangFire.JobQueue.QUEUE 컬럼 값을 결정 → 도메인별 워커 라우팅에 사용.
        var hangfireJobId = _bg.Create(job, new EnqueuedState(t.DefaultQueue));

        _logger.LogJobContext(jobArgs,
            isManual
                ? (effectiveIsTest ? "Enqueued (manual, test)" : "Enqueued (manual)")
                : (effectiveIsTest ? "Enqueued (test)"          : "Enqueued"));

        return new ManualRunResult(t.DomainCode, t.BrandCode, logId, hangfireJobId, t.DefaultQueue, null, effectiveIsTest);
    }

    /// <summary>
    /// 분 단위 정렬 — cron tick 과 BucketedDt 비교가 정확히 일치하도록 초/밀리초를 0 으로.
    /// </summary>
    /// <param name="t">정렬할 시각 (KST 로컬).</param>
    /// <returns>같은 분의 :00.000 으로 잘린 <see cref="DateTimeKind.Local"/> 시각.</returns>
    /// <remarks>
    /// Kind=Local 명시는 Hangfire JSON 직렬화 라운드트립 후 worker 측 비교가 동일 시간대(KST)에서
    /// 이뤄지도록 보장하기 위함.
    /// </remarks>
    private static DateTime TruncateToMinute(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Local);

    /// <summary>
    /// 브랜드 시간대 cron 목록 중 이번 bucket 에 발사 시각인 것이 하나라도 있는지 검사 (OR).
    /// </summary>
    /// <param name="cronExprs">
    /// SCH_DOMAIN_BRAND_CRON 의 활성 cron 목록 (<see cref="TargetRow.CronExprs"/>).
    /// 비어있으면 시간대 설정 없음 → 도메인 L1 스케줄 상속(항상 발사).
    /// </param>
    /// <param name="bucketed">분 단위 정렬된 KST 시각 (<see cref="TruncateToMinute"/> 결과).</param>
    /// <returns>
    /// 목록이 비었거나 (cron 중 하나라도 발사 시각 == bucketed) 일 때 true.
    /// 그 외 false (이번 tick 에서는 이 브랜드를 발사하지 않음).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>2단계 필터 구조</b> — 이 함수는 도메인 RecurringJob 이 fire 된 뒤(= <see cref="Fire"/> 진입) 에만
    /// 호출된다. 도메인 CRON 의 한 tick 이 먼저 울리고, 그 tick 안에서 각 브랜드의 시간대 cron 들이
    /// 추가 narrowing 을 검사한다.
    /// </para>
    /// <para>
    /// <b>유니온(OR) 시멘틱</b> — 브랜드당 논리 타겟은 1개이고 cron 들은 그 발사시각의 합집합이다.
    /// 따라서 두 시간대가 같은 분에 겹쳐도 결과는 "발사한다" 하나로 합쳐져 enqueue 는 0 또는 1회.
    /// 겹침이 무해하므로 등록 단계에서 겹침 검증은 하지 않는다(문법 검증만).
    /// </para>
    /// <para>
    /// <b>narrowing 만 가능</b> — 도메인 CRON 이 5분 간격(<c>*/5 * * * *</c>) 이면 <see cref="Fire"/> 는
    /// :00, :05, :10... 에만 진입한다. 따라서 시간대 cron 을 <c>* * * * *</c> (매분) 로 걸어도
    /// 5분 간격 그대로 — 도메인보다 자주 돌릴 수 없다. 진짜 용도는 narrowing
    /// (예: 도메인은 5분 간격이지만 특정 시간대만 평일 업무시간 정각).
    /// </para>
    /// <para>
    /// <b>KST 단일 시간대 정책</b> — Cronos 의 (DateTimeOffset, TimeZoneInfo) 오버로드 사용.
    /// 단일 인자 GetNextOccurrence(DateTime) 는 Kind=Utc 만 허용하므로 Local 시각을 그대로 넘기면 throw.
    /// TimeZoneInfo.Local(=KST) 로 cron 을 평가. SCH_DOMAIN_BRAND_CRON.TIMEZONE_ID 컬럼은 스키마상
    /// 보존하지만 평가 단계에서는 무시.
    /// </para>
    /// </remarks>
    private static bool IsAnyFireTime(IReadOnlyList<string> cronExprs, DateTime bucketed)
    {
        if (cronExprs.Count == 0) return true;

        // bucketed.Kind=Local → DateTimeOffset 암묵 변환이 로컬 오프셋(KST) 부여.
        // -1분 윈도우는 GetNextOccurrence 의 strictly-greater 시멘틱 보정용 — bucketed 자체가 발사
        // 시각이어도 그대로 넘기면 그 다음 occurrence 가 반환되므로 1분 앞을 기준으로 잡는다.
        DateTimeOffset bucketedOffset = bucketed;
        var prevBoundary = bucketedOffset.AddMinutes(-1);

        foreach (var expr in cronExprs)
        {
            if (string.IsNullOrWhiteSpace(expr)) return true;   // 방어적: 빈 cron 은 상속 취급
            var cron = Cronos.CronExpression.Parse(expr);
            var prev = cron.GetNextOccurrence(prevBoundary, TimeZoneInfo.Local);
            if (prev.HasValue && prev.Value == bucketedOffset) return true;
        }
        return false;
    }
}

/// <summary>
/// "Run now" / "Run domain now" 결과 DTO. 컨트롤러가 그대로 응답에 실어 보낸다.
/// </summary>
/// <param name="DomainCode">SCH_DOMAIN.DOMAIN_CODE.</param>
/// <param name="BrandCode">SCH_BRAND.BRAND_CODE.</param>
/// <param name="LogId">정상 enqueue 시 발급된 SCH_JOB_LOG.LOG_ID. skip 이면 null.</param>
/// <param name="HangfireJobId">정상 enqueue 시 Hangfire 가 발급한 JobId (HangFire.Job.ID). skip 이면 null.</param>
/// <param name="Queue">정상 enqueue 시 사용된 큐 이름 (HangFire.JobQueue.QUEUE). skip 이면 null.</param>
/// <param name="SkipReason">skip 사유 (사람이 읽는 한글 메시지). 정상 enqueue 시 null — 호출자는 이 값으로 분기.</param>
/// <param name="IsTest">
/// 실효 IsTest. cron 자동은 <c>t.IsTest</c>, 수동은 <c>t.IsTest || runAs==Test</c>. 프론트가 토스트에 "테스트 실행됨" 분기에 사용.
/// </param>
/// <param name="ActiveLogId">skip 시 점유 중인 잡의 LogId. 프론트가 토스트/링크에 사용.</param>
/// <param name="ActiveStatus">skip 시 점유 중인 잡의 Status (ENQUEUED/RUNNING).</param>
public record ManualRunResult(
    string  DomainCode,
    string  BrandCode,
    string? LogId,
    string? HangfireJobId,
    string? Queue,
    string? SkipReason,
    bool    IsTest       = false,
    string? ActiveLogId  = null,
    string? ActiveStatus = null);

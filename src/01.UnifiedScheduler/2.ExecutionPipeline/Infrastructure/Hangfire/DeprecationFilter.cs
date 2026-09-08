// [담당업무 2] 필터 단계 — 워커가 잡을 집는 순간 큐 대기 한도를 넘긴 잡을 실행 전에 폐기한다.

using Hangfire.Common;
using Hangfire.States;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Hangfire <see cref="IElectStateFilter"/> — 워커가 Job 을 pickup 해 <see cref="ProcessingState"/> 로
/// 전이하려는 순간에 잡의 신선도(age)를 검사하고, <see cref="JobArgs.DeprecateSec"/> 를 초과하면
/// <see cref="DeletedState"/> 로 강등시켜 워커가 실행하지 못하게 한다.
/// </summary>
/// <remarks>
/// 큐 적체·워커 다운으로 오래 묵은 잡이 뒤늦게 실행되어 실시간성·멱등성을 깨뜨리는 걸 방지하는 게이트.
/// 분 경계 발사를 가정한 도메인 의미론에서 한참 늦게 시작되는 잡은 결과 가치가 없다.
/// <para>
/// 판정 기준은 <see cref="JobArgs.DeprecateSec"/> (큐 대기 한도) 이며 <see cref="JobArgs.TimeoutSec"/>
/// (시작 후 실행 한도) 이 아니다 — 둘은 적정 크기가 반대라서 분리된 컬럼이다.
/// <c>DeprecateSec == 0</c> 은 무제한(폐기 안 함) 이므로 비교 전에 반드시 단락시킨다.
/// </para>
/// <para>
/// 두 시각(<see cref="DatetimeHelper.Now"/> · <see cref="JobArgs.BucketedDt"/>) 모두 KST 로컬이라
/// <see cref="DateTimeKind"/> 무관하게 차이값만 산수 — KST 단일 시간대 정책의 부산물이다.
/// </para>
/// </remarks>
public class DeprecationFilter : IElectStateFilter
{
    private readonly JobLogRepository           _logs;
    private readonly IServiceProvider           _services;
    private readonly ILogger<DeprecationFilter> _logger;

    /// <summary>
    /// <paramref name="services"/> 로 <see cref="INotifier"/> 를 <b>지연 해석</b>하는 이유 — 생성자 주입하면
    /// 기동이 깨진다.
    /// <para>
    /// 본 필터는 <c>AddHangfire((sp, cfg) =&gt; ... sp.GetRequiredService&lt;DeprecationFilter&gt;())</c> 람다,
    /// 즉 <c>IGlobalConfiguration</c> 싱글턴 팩토리가 실행되는 도중에 resolve 된다. 그런데 <see cref="INotifier"/>
    /// 구현체 <see cref="HangfireNotifier"/> 는 <c>IBackgroundJobClient</c> 를 요구하고, Hangfire 는 모든 자기
    /// 서비스를 <c>TryAddSingletonChecked</c> 로 등록해 해석 시마다 <c>IGlobalConfiguration</c> 을 강제로 당긴다.
    /// 결국 아직 캐시되지 않은 <c>IGlobalConfiguration</c> 팩토리가 자기 안에서 재진입해
    /// <c>UseSqlServerStorage</c>·<c>UseFilter</c> 가 반복 실행된다 (스키마 설치 로그 반복 + 전역 필터 중복 등록).
    /// </para>
    /// <para>
    /// 알림은 잡이 실제로 폐기되는 시점 — 기동이 한참 끝난 뒤 — 에만 필요하므로, 그때 해석하면 고리가 끊긴다.
    /// <see cref="Jobs.JobBase{TSelf,TParam}"/> 가 추가 의존성에 쓰는 방식과 동일하다.
    /// <b>생성자 주입으로 되돌리지 말 것.</b>
    /// </para>
    /// </summary>
    public DeprecationFilter(
        JobLogRepository           logs,
        IServiceProvider           services,
        ILogger<DeprecationFilter> logger)
    {
        _logs     = logs;
        _services = services;
        _logger   = logger;
    }

    /// <summary>
    /// 후보 상태가 <see cref="ProcessingState"/> 일 때만 age 검사를 수행. 초과 시
    /// <see cref="ElectStateContext.CandidateState"/> 를 <see cref="DeletedState"/> 로 교체하고
    /// SCH_JOB_LOG 의 STATUS 를 DEPRECATED 로 마킹한 뒤 개발팀에 메일을 통지한다.
    /// </summary>
    /// <param name="context">
    /// Hangfire 상태 전이 컨텍스트. <see cref="ElectStateContext.CandidateState"/> 를 다른
    /// <see cref="IState"/> 로 교체해 전이를 가로챌 수 있다.
    /// </param>
    public void OnStateElection(ElectStateContext context)
    {
        // EnqueuedState→ProcessingState 외의 전이(SUCCESS/FAILED/Scheduled 등)는 신선도와 무관 → 통과.
        if (context.CandidateState is not ProcessingState) return;

        // JobArgs 없는 인프라 잡(Dispatcher/Sync/Sweeper)은 timeout 개념이 없어 검사 대상 아님.
        var args = ExtractJobArgs(context.BackgroundJob.Job);
        if (args == null) return;

        // DeprecateSec == 0 은 "이 스케줄/브랜드는 큐 대기 폐기를 하지 않음" → 가드 없이 비교하면
        // age > 0 이 항상 참이라 모든 잡이 즉시 폐기된다.
        var ageSec = (DatetimeHelper.Now - args.BucketedDt).TotalSeconds;
        if (args.DeprecateSec <= 0 || ageSec <= args.DeprecateSec) return;

        // [전이 가로채기] 부수효과보다 먼저 — 아래 마킹/로그/메일이 어떻게 되든 워커는 이 잡을 실행하면 안 된다.
        context.CandidateState = new DeletedState
        {
            Reason = $"DEPRECATED: age={ageSec:F0}s > deprecate={args.DeprecateSec}s"
        };

        var detail = $"age={ageSec:F0}s > deprecate={args.DeprecateSec}s, hangfireJobId={context.BackgroundJob.Id}";

        // [부수효과 격리] 본 메서드는 Hangfire 상태 전이 파이프라인 한복판에서 돈다 — 여기서 예외가 새어나가면
        // 전이 자체가 깨져 잡이 애매한 상태로 남는다. DB 마킹·로그·메일은 모두 자체 예외를 swallow 한다
        // (JobBase 의 Safe* 래퍼와 동일 원칙).
        SafeMarkDeprecated(args, detail);
        SafeNotify(args, detail);
    }

    /// <summary>
    /// SCH_JOB_LOG STATUS → DEPRECATED 마킹 + Seq 로 보낼 warning 로그. 예외는 swallow —
    /// 마킹에 실패해도 <see cref="Jobs.DeprecationSweeper"/> 가 다음 스윕에서 stale row 를 마감한다.
    /// </summary>
    private void SafeMarkDeprecated(JobArgs args, string detail)
    {
        try
        {
            _logs.MarkDeprecated(args.LogId);

            // Seq 에서 LogId 로 바로 찾아갈 수 있도록 JobLog 경유 — Domain/Brand/Center/LogId/Bucket 이
            // 구조화 속성으로 실린다. 폐기는 "실행됐어야 할 잡이 안 돈" 상황이라 Warning 레벨.
            _logger.WarnLogJobContext(args, $"DEPRECATED before start ({detail})");
        }
        catch (Exception ex)
        {
            _logger.ErrorLogJobContext(args, ex,
                "DEPRECATED before start — MarkDeprecated threw, sweeper will reconcile");
        }
    }

    /// <summary>
    /// 폐기 사실을 개발팀에 메일 통지. <see cref="INotifier"/> 는 알림 전용 큐로 enqueue 만 하므로
    /// 상태 전이 경로가 SMTP 응답을 기다리지 않는다. 발송 자체의 예외는 swallow.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>IsBusiness=false</c> — 폐기는 외부 시스템이 돌려준 업무 실패가 아니라 큐 적체/워커 지연이라는
    ///         인프라 사건이다. 이 값이 false 면 <see cref="Jobs.NotificationJob"/> 이 브랜드 담당자 경로를 아예 타지 않는다.</item>
    ///   <item><c>SystemMailSend</c> — enqueue 시점 스냅샷(도메인 MAIL_SEND) 을 그대로 존중. 'N' 인 도메인은 메일이 안 나간다.</item>
    ///   <item><c>BrandMailSend=false</c> — 브랜드 담당자에게는 보내지 않는다는 의도를 값으로도 못 박는다.</item>
    /// </list>
    /// 잡 1건당 메일 1통이라 큐 적체가 대량으로 풀리는 순간에는 그만큼 메일이 나간다 — 폐기를 건별로
    /// 알겠다는 운영 정책의 대가이므로, 노이즈가 문제되면 여기서 도메인 단위 쿨다운을 두면 된다.
    /// </remarks>
    private void SafeNotify(JobArgs args, string detail)
    {
        try
        {
            // 지연 해석 — 생성자 주입 금지. 이유는 생성자 XML 주석 참조.
            _services.GetRequiredService<INotifier>().Send(new NotificationRequest
            {
                Subject    = $"[{args.DomainCode}/{args.BrandCode}] Job deprecated before start",
                JobName    = nameof(DeprecationFilter),
                DomainCode = args.DomainCode,
                BrandCode  = args.BrandCode,
                CenterCode = args.CenterCode,
                LogId      = args.LogId,
                Requester  = args.Requester,
                IsTest     = args.IsTest,
                ErrorType  = "Deprecated",
                IsBusiness = false,
                Message    = $"Queue wait limit exceeded — {detail}. "
                           + $"Scheduled={args.BucketedDt:yyyy-MM-dd HH:mm:ss}. Worker picked it up too late; execution was cancelled.",
                SystemMailSend = args.SystemMailSend,
                BrandMailSend  = false,
            });
        }
        catch (Exception ex)
        {
            _logger.ErrorLogJobContext(args, ex,
                "DEPRECATED before start — Notifier.Send threw, mail skipped");
        }
    }

    private static JobArgs? ExtractJobArgs(Job job) => job.Args?.FirstOrDefault() as JobArgs;
}

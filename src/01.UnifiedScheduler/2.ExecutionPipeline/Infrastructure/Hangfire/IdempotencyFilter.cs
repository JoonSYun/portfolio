// [담당업무 2] 필터 단계 — 실행 전 SCH_JOB_LOG INSERT 로 (Domain, Brand, LogId) 중복 실행을 차단한다.

using Hangfire.Client;
using Hangfire.Common;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Hangfire <see cref="IClientFilter"/> — enqueue 경로의 멱등성 가드.
/// 같은 <see cref="JobArgs.LogId"/> 로 두 번 enqueue 되는 경합을 SCH_JOB_LOG.LOG_ID UNIQUE INSERT 로 차단한다.
/// 충돌 시 Hangfire 큐 INSERT 자체를 취소하고 기존 row 의 STATUS 를 SKIPPED_DUP 으로 마킹.
/// </summary>
/// <remarks>
/// <see cref="Jobs.Dispatcher"/>·<see cref="Jobs.ScheduleSyncJob"/>·<see cref="Jobs.DeprecationSweeper"/> 같은
/// 인프라 잡은 <see cref="JobArgs"/> 인자가 없어서 검사 우회 — 도메인 잡(<see cref="Jobs.IDomainJob"/> 구현체) 만
/// 멱등 검사를 받는다. SCH_JOB_LOG.LOG_ID 의 UNIQUE 제약이 single source of truth 이며, 이 필터는 그것을
/// enqueue 단계에서 미리 잡아 큐 오염을 막는 역할.
/// </remarks>
public class IdempotencyFilter : IClientFilter
{
    private readonly JobLogRepository _logs;

    public IdempotencyFilter(JobLogRepository logs) { _logs = logs; }

    /// <summary>
    /// 큐 INSERT 직전 호출. <see cref="JobArgs.LogId"/> 로 SCH_JOB_LOG INSERT 를 시도하고
    /// UNIQUE 충돌 시 enqueue 를 취소 + 기존 row 의 STATUS 를 SKIPPED_DUP 으로 전이.
    /// </summary>
    /// <param name="context">
    /// Hangfire enqueue 컨텍스트. <see cref="CreatingContext.Canceled"/> 를 true 로 설정하면 큐 INSERT 가 일어나지 않는다.
    /// </param>
    public void OnCreating(CreatingContext context)
    {
        // Dispatcher / ScheduleSyncJob / DeprecationSweeper 등 JobArgs 없는 인프라 잡은 검사 대상 아님.
        var args = ExtractJobArgs(context.Job);
        if (args == null) return;

        var inserted = _logs.TryInsertEnqueued(args, hangfireJobId: null);
        if (!inserted)
        {
            context.Canceled = true;
            _logs.LogSkippedDup(args.LogId);
        }
    }

    /// <summary>
    /// 큐 INSERT 성공 직후 호출. Hangfire 가 발급한 <see cref="Hangfire.BackgroundJob.Id"/> 를
    /// 기존 SCH_JOB_LOG row 의 HANGFIRE_JOB_ID 컬럼에 UPDATE 해 두 식별자를 묶는다 —
    /// Admin UI / Hangfire Dashboard 에서 양방향 추적이 가능해진다.
    /// </summary>
    /// <param name="context">
    /// Hangfire enqueue 결과 컨텍스트. <see cref="CreatedContext.BackgroundJob"/> 가 null 이면
    /// (이전 단계에서 취소된 enqueue) 아무 동작도 하지 않는다.
    /// </param>
    public void OnCreated(CreatedContext context)
    {
        var args = ExtractJobArgs(context.Job);
        if (args == null || context.BackgroundJob == null) return;
        _logs.UpdateHangfireJobId(args.LogId, context.BackgroundJob.Id);
    }

    private static JobArgs? ExtractJobArgs(Job job) => job.Args?.FirstOrDefault() as JobArgs;
}

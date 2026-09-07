namespace Portfolio.UnifiedScheduler.ExecutionPipeline;

/// <summary>실행 상태. 전이 규칙은 <see cref="JobStateMachine"/> 이 강제한다.</summary>
public enum JobExecutionState
{
    Queued,
    Validating,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Skipped,      // 게이트에 의해 실행되지 않음
    Validated     // 검증 모드로 부작용 없이 통과
}

/// <summary>
/// [담당업무 2] 상태 전이를 공통 베이스로 통일. 파생 Job 은 상태를 직접 만지지 못한다.
/// 허용되지 않은 전이는 예외 — 이력에 "Running 인데 Queued" 같은 불가능한 상태가 남지 않는다.
/// </summary>
public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobExecutionState, JobExecutionState[]> Allowed = new Dictionary<JobExecutionState, JobExecutionState[]>
    {
        [JobExecutionState.Queued]     = new[] { JobExecutionState.Validating, JobExecutionState.Skipped },
        [JobExecutionState.Validating] = new[] { JobExecutionState.Running, JobExecutionState.Failed, JobExecutionState.Validated },
        [JobExecutionState.Running]    = new[] { JobExecutionState.Succeeded, JobExecutionState.Failed, JobExecutionState.TimedOut },
    };

    public static JobExecutionState Transition(JobExecutionState from, JobExecutionState to)
    {
        if (!Allowed.TryGetValue(from, out var next) || !next.Contains(to))
            throw new InvalidOperationException($"허용되지 않은 상태 전이: {from} → {to}");
        return to;
    }

    public static bool IsTerminal(JobExecutionState s) =>
        s is JobExecutionState.Succeeded or JobExecutionState.Failed or JobExecutionState.TimedOut
          or JobExecutionState.Skipped or JobExecutionState.Validated;
}

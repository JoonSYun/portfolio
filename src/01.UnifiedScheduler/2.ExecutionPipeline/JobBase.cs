using System.Diagnostics;
using Polly;
using Polly.Retry;
using Serilog.Context;
using Portfolio.UnifiedScheduler.ConfigurationInheritance;
using Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.History;

namespace Portfolio.UnifiedScheduler.ExecutionPipeline;

/// <summary>
/// [담당업무 2] 공통 Job 베이스 — 실행 라이프사이클 전 구간을 여기서 통일한다.
///
///   로그 부착 → 검증 → 상태 전이 → (타임아웃 전파) 실행 → 실패 마감 → 알림
///
/// 파생 Job 69종은 <see cref="ExecuteCoreAsync"/>(그리고 필요하면 <see cref="ValidateAsync"/>)만
/// 구현한다. 재시도·타임아웃·이력·알림을 개별 Job 이 다시 짜지 않는다 → 확장 비용 최소화.
/// </summary>
public abstract class JobBase
{
    protected ILogger Logger { get; }
    private readonly IExecutionHistoryWriter _history;
    private readonly IJobNotifier _notifier;

    protected JobBase(ILogger logger, IExecutionHistoryWriter history, IJobNotifier notifier)
    {
        Logger = logger; _history = history; _notifier = notifier;
    }

    /// <summary>6. Execute → 7. Finalize → 8. Notify 를 하나의 템플릿으로 고정.</summary>
    public async Task<JobOutcome> RunAsync(JobRequest request, CancellationToken hostCt)
    {
        var s = request.Schedule;
        var executionId = Guid.NewGuid();
        var state = JobExecutionState.Queued;
        var sw = Stopwatch.StartNew();

        // ① 로그 부착 — 이후 모든 로그 라인에 도메인/브랜드/실행ID 가 자동으로 붙는다 (Serilog → Seq 에서 바로 필터)
        using var _1 = LogContext.PushProperty("Domain", s.DomainCode);
        using var _2 = LogContext.PushProperty("Brand", s.BrandCode);
        using var _3 = LogContext.PushProperty("ExecutionId", executionId);

        await _history.StartedAsync(executionId, s, request.FiredAt);

        try
        {
            // ② 검증
            state = JobStateMachine.Transition(state, JobExecutionState.Validating);
            var validation = await ValidateAsync(s);
            if (validation is not null)
                return await FinalizeAsync(executionId, s, JobStateMachine.Transition(state, JobExecutionState.Failed), sw, validation.Reason);

            // 검증 모드 — 부작용 없이 여기서 종료
            if (request.ValidationOnly)
                return await FinalizeAsync(executionId, s, JobStateMachine.Transition(state, JobExecutionState.Validated), sw, "검증 모드 통과");

            // ③ 상태 전이 → ④ 타임아웃 전파 + 재시도 → 실행
            state = JobStateMachine.Transition(state, JobExecutionState.Running);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
            timeout.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));

            var result = await RetryPipeline(s).ExecuteAsync(async ct => await ExecuteCoreAsync(s, ct), timeout.Token);

            return await FinalizeAsync(executionId, s, JobStateMachine.Transition(state, JobExecutionState.Succeeded), sw, result.Message, result.ProcessedCount);
        }
        catch (OperationCanceledException) when (!hostCt.IsCancellationRequested)
        {
            // ⑤ 실패 마감 — 타임아웃
            return await FinalizeAsync(executionId, s, JobStateMachine.Transition(state, JobExecutionState.TimedOut), sw, $"타임아웃 {s.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "실행 실패");
            return await FinalizeAsync(executionId, s, JobStateMachine.Transition(state, JobExecutionState.Failed), sw, ex.Message);
        }
    }

    // ---- 파생 Job 이 구현하는 부분 --------------------------------------------

    /// <summary>선택: 실행 전 검증. 실패 사유를 반환하면 실행하지 않고 마감한다.</summary>
    protected virtual Task<ValidationFailure?> ValidateAsync(EffectiveSchedule schedule) => Task.FromResult<ValidationFailure?>(null);

    /// <summary>필수: 비즈니스 로직. 이것만 구현하면 파이프라인에 편입된다.</summary>
    protected abstract Task<CoreResult> ExecuteCoreAsync(EffectiveSchedule schedule, CancellationToken ct);

    // ---- 공통 내부 --------------------------------------------------------------

    private static ResiliencePipeline RetryPipeline(EffectiveSchedule s) => new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = s.MaxRetry,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException)
        })
        .Build();

    private async Task<JobOutcome> FinalizeAsync(Guid id, EffectiveSchedule s, JobExecutionState final, Stopwatch sw, string? message, int processed = 0)
    {
        sw.Stop();
        var outcome = new JobOutcome(id, s.DomainCode, s.BrandCode, final, sw.Elapsed, processed, message);
        await _history.FinishedAsync(outcome);               // 7. Finalize
        await _notifier.EnqueueAsync(outcome);               // 8. Notify — 알림 전용 워커로 넘김
        Logger.LogInformation("[Finalize] {State} {Elapsed}ms {Message}", final, sw.ElapsedMilliseconds, message);
        return outcome;
    }
}

public sealed record ValidationFailure(string Reason);
public sealed record CoreResult(int ProcessedCount, string? Message = null);
public sealed record JobOutcome(Guid ExecutionId, string DomainCode, string BrandCode, JobExecutionState State,
    TimeSpan Elapsed, int ProcessedCount, string? Message);

public interface IJobNotifier { Task EnqueueAsync(JobOutcome outcome); }

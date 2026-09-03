using System.Diagnostics;
using FlowForge.Abstractions.Configuration;
using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Operations;
using FlowForge.Abstractions.Resilience;
using Microsoft.Extensions.Logging;

namespace FlowForge.Core.Jobs;

// The behavior pipeline (outermost → innermost by Order):
//   Logging(0) → History(10) → Gate(20) → Idempotency(30) → Retry(40) → Timeout(50) → handler
// Each cross-cutting concern lives here once and applies to every job type.

/// <summary>Structured start/finish/duration logging around every execution.</summary>
public sealed class LoggingBehavior : IJobBehavior
{
    private readonly ILogger<LoggingBehavior> _logger;
    public LoggingBehavior(ILogger<LoggingBehavior> logger) => _logger = logger;

    public int Order => 0;

    public async Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("▶ {Job}/{Tenant} exec={Exec}", context.JobKey, context.TenantId, context.ExecutionId);
        try
        {
            var result = await next(context);
            _logger.Log(result.IsSuccess ? LogLevel.Information : LogLevel.Warning,
                "◼ {Job}/{Tenant} {Status} ({Processed} items) in {Ms}ms {Message}",
                context.JobKey, context.TenantId, result.Status, result.ProcessedCount, sw.ElapsedMilliseconds, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✖ {Job}/{Tenant} threw after {Ms}ms", context.JobKey, context.TenantId, sw.ElapsedMilliseconds);
            throw;
        }
    }
}

/// <summary>Writes the execution-history row that the admin console reads.</summary>
public sealed class ExecutionHistoryBehavior : IJobBehavior
{
    private readonly IExecutionStore _store;
    public ExecutionHistoryBehavior(IExecutionStore store) => _store = store;

    public int Order => 10;

    public async Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await _store.AppendAsync(new ExecutionRecord
        {
            ExecutionId = context.ExecutionId,
            JobKey = context.JobKey,
            TenantId = context.TenantId,
            Status = JobStatus.Running,
            StartedAt = startedAt,
            Attempt = context.Attempt
        });

        try
        {
            var result = await next(context);
            await _store.UpdateAsync(Finalize(context, startedAt, result.Status, result.ProcessedCount, result.Message));
            return result;
        }
        catch (Exception ex)
        {
            await _store.UpdateAsync(Finalize(context, startedAt, JobStatus.Failed, 0, ex.Message));
            throw;
        }
    }

    private static ExecutionRecord Finalize(JobContext ctx, DateTimeOffset startedAt, JobStatus status, int processed, string? message) => new()
    {
        ExecutionId = ctx.ExecutionId,
        JobKey = ctx.JobKey,
        TenantId = ctx.TenantId,
        Status = status,
        StartedAt = startedAt,
        FinishedAt = DateTimeOffset.UtcNow,
        Attempt = ctx.Attempt,
        ProcessedCount = processed,
        Message = message
    };
}

/// <summary>Applies the three-tier operational gate before any work runs.</summary>
public sealed class OperationalGateBehavior : IJobBehavior
{
    private readonly IOperationalGate _gate;
    public OperationalGateBehavior(IOperationalGate gate) => _gate = gate;

    public int Order => 20;

    public async Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next)
    {
        switch (_gate.Evaluate(context.JobKey, context.TenantId))
        {
            case GateDecision.Paused:
                return JobResult.Skipped("Skipped — platform is globally paused.");
            case GateDecision.Blocked:
                return JobResult.Skipped($"Skipped — {context.JobKey}/{context.TenantId} is blocked.");
            case GateDecision.ValidationOnly:
                return await next(context with { IsValidationMode = true });
            default:
                return await next(context);
        }
    }
}

/// <summary>Double idempotency guard: skips duplicates, releases the claim on failure so a retry can proceed.</summary>
public sealed class IdempotencyBehavior : IJobBehavior
{
    private readonly IIdempotencyStore _store;
    public IdempotencyBehavior(IIdempotencyStore store) => _store = store;

    public int Order => 30;

    public async Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next)
    {
        var key = $"{context.JobKey}:{context.TenantId}:{context.ExecutionId}";
        if (!await _store.TryClaimAsync(key, context.Cancellation))
            return JobResult.Skipped("Skipped — duplicate execution (idempotent).");

        try
        {
            var result = await next(context);
            if (result.IsSuccess)
                await _store.CompleteAsync(key, context.Cancellation);
            else
                await _store.ReleaseAsync(key, context.Cancellation);
            return result;
        }
        catch
        {
            await _store.ReleaseAsync(key, context.Cancellation);
            throw;
        }
    }
}

/// <summary>Retries per the effective <see cref="RetryPolicy"/>, giving each attempt its own timeout (Timeout sits inside).</summary>
public sealed class RetryBehavior : IJobBehavior
{
    private readonly ILogger<RetryBehavior> _logger;
    public RetryBehavior(ILogger<RetryBehavior> logger) => _logger = logger;

    public int Order => 40;

    public async Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next)
    {
        var policy = context.Config.Retry;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            var delay = policy.DelayFor(attempt);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, context.Cancellation);

            try
            {
                var result = await next(context with { Attempt = attempt });
                if (result.IsSuccess || attempt == policy.MaxAttempts)
                    return result;

                _logger.LogWarning("Retry {Attempt}/{Max} for {Job}/{Tenant}: {Message}",
                    attempt, policy.MaxAttempts, context.JobKey, context.TenantId, result.Message);
            }
            catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt == policy.MaxAttempts)
                    throw;
                _logger.LogWarning(ex, "Retry {Attempt}/{Max} for {Job}/{Tenant} after exception",
                    attempt, policy.MaxAttempts, context.JobKey, context.TenantId);
            }
        }

        return lastException is not null
            ? JobResult.Failure(lastException.Message)
            : JobResult.Failure("Retry attempts exhausted.");
    }
}

/// <summary>Bounds each attempt with the effective timeout, mapping cancellation to a failed result.</summary>
public sealed class TimeoutBehavior : IJobBehavior
{
    public int Order => 50;

    public async Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        cts.CancelAfter(context.Config.Timeout);

        try
        {
            return await next(context with { Cancellation = cts.Token });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !context.Cancellation.IsCancellationRequested)
        {
            return JobResult.Failure($"Timed out after {context.Config.Timeout.TotalSeconds:0.#}s.");
        }
    }
}

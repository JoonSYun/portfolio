using FlowForge.Abstractions.Configuration;

namespace FlowForge.Abstractions.Jobs;

/// <summary>Lifecycle state of a single job execution.</summary>
public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Compensated,
    Skipped
}

/// <summary>
/// Everything a handler needs to run one execution. Immutable; the runner builds
/// a fresh context per (jobKey, tenant, execution).
/// </summary>
public sealed record JobContext
{
    public required string JobKey { get; init; }
    public required string TenantId { get; init; }
    public required string ExecutionId { get; init; }
    public required EffectiveJobConfig Config { get; init; }

    public DateTimeOffset ScheduledFor { get; init; }
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// When true the handler must exercise its full path but must not commit any
    /// external side effect. This backs the operator "validation mode" (dry-run).
    /// </summary>
    public bool IsValidationMode { get; init; }

    public IReadOnlyDictionary<string, string> Parameters => Config.Parameters;

    public CancellationToken Cancellation { get; init; }

    public string Param(string key, string fallback = "") =>
        Parameters.TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>Outcome of one execution.</summary>
public sealed record JobResult(JobStatus Status, string? Message = null, int ProcessedCount = 0)
{
    public bool IsSuccess => Status is JobStatus.Succeeded or JobStatus.Skipped;

    public static JobResult Success(int processed = 0, string? message = null) =>
        new(JobStatus.Succeeded, message, processed);

    public static JobResult Skipped(string message) =>
        new(JobStatus.Skipped, message);

    public static JobResult Failure(string message) =>
        new(JobStatus.Failed, message);
}

/// <summary>
/// The unit of business logic. Everything cross-cutting (logging, validation,
/// timeout, retry, idempotency, state transitions) is handled by the framework
/// around this call, so a concrete job only implements what is unique to it.
/// </summary>
public interface IJob
{
    Task<JobResult> ExecuteAsync(JobContext context);
}

/// <summary>The next step in the behavior pipeline.</summary>
public delegate Task<JobResult> JobExecutionDelegate(JobContext context);

/// <summary>
/// A cross-cutting concern wrapped around job execution, composed as a pipeline
/// (outermost first). Behaviors are how the "common job base" concerns are
/// applied uniformly to all 1..N job types.
/// </summary>
public interface IJobBehavior
{
    int Order { get; }
    Task<JobResult> HandleAsync(JobContext context, JobExecutionDelegate next);
}

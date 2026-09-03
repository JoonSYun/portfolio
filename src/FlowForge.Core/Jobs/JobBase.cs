using FlowForge.Abstractions.Jobs;
using Microsoft.Extensions.Logging;

namespace FlowForge.Core.Jobs;

/// <summary>
/// Common base for every job. It fixes the per-job shape — validate, then execute
/// core business logic, with a compensation hook — while the surrounding pipeline
/// (logging, gating, idempotency, retry, timeout, history) is applied uniformly by
/// the framework. A concrete job overrides <see cref="ExecuteCoreAsync"/> and,
/// optionally, <see cref="ValidateAsync"/> / <see cref="CompensateAsync"/> — nothing else.
/// </summary>
public abstract class JobBase : IJob
{
    protected JobBase(ILogger logger) => Logger = logger;

    protected ILogger Logger { get; }

    public async Task<JobResult> ExecuteAsync(JobContext context)
    {
        var validation = await ValidateAsync(context);
        if (validation is not null)
            return validation;

        if (context.IsValidationMode)
        {
            Logger.LogInformation(
                "[validation] {Job}/{Tenant} passed validation; side effects suppressed.",
                context.JobKey, context.TenantId);
            return JobResult.Skipped("Validation mode — side effects suppressed.");
        }

        return await ExecuteCoreAsync(context);
    }

    /// <summary>
    /// Optional pre-flight check. Return a non-null <see cref="JobResult"/> to
    /// short-circuit (e.g. <c>JobResult.Failure</c> on invalid parameters); return
    /// null to proceed.
    /// </summary>
    protected virtual Task<JobResult?> ValidateAsync(JobContext context) =>
        Task.FromResult<JobResult?>(null);

    /// <summary>The unique business logic of this job.</summary>
    protected abstract Task<JobResult> ExecuteCoreAsync(JobContext context);

    /// <summary>Optional compensation, invoked by a saga when a later step fails.</summary>
    public virtual Task CompensateAsync(JobContext context) => Task.CompletedTask;
}

using FlowForge.Abstractions.Configuration;
using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowForge.Core.Jobs;

/// <summary>
/// Turns an inbound <see cref="JobMessage"/> into a fully-governed execution:
/// resolve effective config → build context → run the behavior pipeline → invoke
/// the handler (resolved in its own DI scope). The pipeline is composed once and
/// reused for every message.
/// </summary>
public sealed class JobRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigResolver _resolver;
    private readonly JobRegistry _registry;
    private readonly IJobBehavior[] _behaviors;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IConfigResolver resolver,
        JobRegistry registry,
        IEnumerable<IJobBehavior> behaviors,
        ILogger<JobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _resolver = resolver;
        _registry = registry;
        _behaviors = behaviors.OrderBy(b => b.Order).ToArray();
        _logger = logger;
    }

    public async Task RunAsync(JobMessage message, CancellationToken ct)
    {
        EffectiveJobConfig config;
        try
        {
            config = await _resolver.ResolveAsync(message.JobKey, message.TenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve config for {Job}/{Tenant}; dropping message {MessageId}.",
                message.JobKey, message.TenantId, message.MessageId);
            return;
        }

        if (!config.Enabled)
        {
            _logger.LogInformation("{Job}/{Tenant} is disabled; skipping.", message.JobKey, message.TenantId);
            return;
        }

        var context = new JobContext
        {
            JobKey = message.JobKey,
            TenantId = message.TenantId,
            ExecutionId = message.ExecutionId,
            Config = config,
            ScheduledFor = message.ScheduledFor,
            Attempt = message.Attempt,
            Cancellation = ct
        };

        JobExecutionDelegate terminal = async ctx =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = _registry.Resolve(scope.ServiceProvider, ctx.JobKey);
            return await handler.ExecuteAsync(ctx);
        };

        var pipeline = Enumerable
            .Reverse(_behaviors)
            .Aggregate(terminal, (next, behavior) => ctx => behavior.HandleAsync(ctx, next));

        try
        {
            await pipeline(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled failure running {Job}/{Tenant} exec={Exec}.",
                message.JobKey, message.TenantId, message.ExecutionId);
        }
    }
}

/// <summary>Builds and publishes a <see cref="JobMessage"/> — used by the scheduler and by manual triggers.</summary>
public interface IJobDispatcher
{
    Task<string> DispatchAsync(
        string jobKey,
        string tenantId,
        DateTimeOffset scheduledFor,
        string? idempotencyKey = null,
        CancellationToken ct = default);
}

public sealed class JobDispatcher : IJobDispatcher
{
    private readonly IMessageBus _bus;
    public JobDispatcher(IMessageBus bus) => _bus = bus;

    public async Task<string> DispatchAsync(
        string jobKey,
        string tenantId,
        DateTimeOffset scheduledFor,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var executionId = Guid.NewGuid().ToString("n");
        await _bus.PublishAsync(new JobMessage
        {
            MessageId = Guid.NewGuid().ToString("n"),
            JobKey = jobKey,
            TenantId = tenantId,
            ExecutionId = executionId,
            EnqueuedAt = DateTimeOffset.UtcNow,
            ScheduledFor = scheduledFor,
            IdempotencyKey = idempotencyKey
        }, ct);
        return executionId;
    }
}

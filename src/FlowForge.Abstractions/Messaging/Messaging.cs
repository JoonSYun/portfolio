namespace FlowForge.Abstractions.Messaging;

/// <summary>
/// Marks a job handler and declares the job key it serves. The binding engine
/// discovers decorated types by reflection and wires them into both the
/// scheduler registry and the message bus — adding a new domain is "write the
/// handler, add the attribute", with no wiring code to touch.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobHandlerAttribute : Attribute
{
    public string JobKey { get; }

    public JobHandlerAttribute(string jobKey)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
            throw new ArgumentException("Job key is required.", nameof(jobKey));
        JobKey = jobKey;
    }
}

/// <summary>
/// The envelope that travels through the queue. Carries the identity needed for
/// idempotency (MessageId, IdempotencyKey) and for routing (JobKey, TenantId).
/// </summary>
public sealed record JobMessage
{
    public required string MessageId { get; init; }
    public required string JobKey { get; init; }
    public required string TenantId { get; init; }
    public required string ExecutionId { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public DateTimeOffset ScheduledFor { get; init; }
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Business-level dedup key. Two messages with the same key are the "same"
    /// unit of work even if their MessageId differs (the second idempotency check).
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Abstraction over the transport. The reference build ships an in-process bus so
/// the whole platform runs with zero infrastructure; a RabbitMQ/MassTransit
/// adapter implements the same interface for production (see docs/ARCHITECTURE.md).
/// </summary>
public interface IMessageBus
{
    Task PublishAsync(JobMessage message, CancellationToken ct = default);

    /// <summary>Bind a handler to a job key (analogous to an exchange→queue binding).</summary>
    void Subscribe(string jobKey, Func<JobMessage, CancellationToken, Task> handler);
}

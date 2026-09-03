namespace FlowForge.Abstractions.Orchestration;

/// <summary>One atomic piece of work inside a large job (e.g. a single order).</summary>
public sealed record WorkItem(string Id, IReadOnlyDictionary<string, string> Payload);

/// <summary>
/// A batch of work items processed as a unit. Large jobs are split Job → Chunk →
/// Step so they can run in parallel with bounded concurrency and so failure is
/// isolated to a chunk rather than the whole job.
/// </summary>
public sealed record Chunk(int Index, IReadOnlyList<WorkItem> Items);

/// <summary>Per-item outcome within a chunk.</summary>
public sealed record StepResult(string ItemId, bool Success, string? Error = null);

/// <summary>Aggregated result of processing one chunk.</summary>
public sealed record ChunkResult(int Index, IReadOnlyList<StepResult> Steps)
{
    public int Succeeded => Steps.Count(s => s.Success);
    public int Failed => Steps.Count(s => !s.Success);
    public bool AllSucceeded => Steps.All(s => s.Success);
}

/// <summary>
/// A step in a saga: a forward action paired with its compensation. If any later
/// step fails, the runner invokes <see cref="CompensateAsync"/> on every step
/// that had already completed, in reverse order.
/// </summary>
public interface ISagaStep<in TState>
{
    string Name { get; }
    Task ExecuteAsync(TState state, CancellationToken ct);
    Task CompensateAsync(TState state, CancellationToken ct);
}

/// <summary>Terminal state of a saga run.</summary>
public enum SagaOutcome
{
    Completed,
    Compensated,
    CompensationFailed
}

/// <summary>Result of running a saga, including which steps ran and which were compensated.</summary>
public sealed record SagaResult(
    SagaOutcome Outcome,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> CompensatedSteps,
    string? FailedStep = null,
    string? Error = null);

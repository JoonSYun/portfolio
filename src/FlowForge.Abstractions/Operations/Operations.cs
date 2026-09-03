using FlowForge.Abstractions.Jobs;

namespace FlowForge.Abstractions.Operations;

/// <summary>A single execution history entry, as shown on the admin console.</summary>
public sealed record ExecutionRecord
{
    public required string ExecutionId { get; init; }
    public required string JobKey { get; init; }
    public required string TenantId { get; init; }
    public required JobStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int Attempt { get; init; }
    public int ProcessedCount { get; init; }
    public string? Message { get; init; }

    public double? DurationMs =>
        FinishedAt is { } f ? (f - StartedAt).TotalMilliseconds : null;
}

/// <summary>Append-only-ish store of execution history (in-memory here, a table in production).</summary>
public interface IExecutionStore
{
    Task AppendAsync(ExecutionRecord record, CancellationToken ct = default);
    Task UpdateAsync(ExecutionRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionRecord>> QueryAsync(
        string? jobKey = null, string? tenantId = null, int limit = 100, CancellationToken ct = default);
}

/// <summary>The three-level operator decision returned by the gate.</summary>
public enum GateDecision
{
    /// <summary>Run normally.</summary>
    Allow,

    /// <summary>Blocked at domain/tenant level — skip this execution.</summary>
    Blocked,

    /// <summary>Global pause is in effect — skip everything.</summary>
    Paused,

    /// <summary>Run in validation (dry-run) mode — exercise the path, commit nothing.</summary>
    ValidationOnly
}

/// <summary>Snapshot of operator state for the admin console.</summary>
public sealed record OperationalStatus
{
    public required bool GloballyPaused { get; init; }
    public required bool ValidationMode { get; init; }
    public required IReadOnlyList<string> BlockedKeys { get; init; }
}

/// <summary>
/// Three-tier operational control, exercised before every execution:
///   1) global pause (external maintenance),
///   2) domain/tenant block (partial exclusion),
///   3) validation mode (dry-run a new domain in production safely).
/// The point is that an operator can intervene without a developer or a deploy.
/// </summary>
public interface IOperationalGate
{
    GateDecision Evaluate(string jobKey, string tenantId);

    void PauseGlobally();
    void ResumeGlobally();
    void Block(string jobKey, string? tenantId = null);
    void Unblock(string jobKey, string? tenantId = null);
    void SetValidationMode(bool enabled);

    OperationalStatus Snapshot();
}

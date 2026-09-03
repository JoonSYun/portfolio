using FlowForge.Abstractions.Orchestration;
using Microsoft.Extensions.Logging;

namespace FlowForge.Core.Orchestration;

/// <summary>
/// Runs a linear saga: execute steps forward; if any step fails, compensate every
/// already-completed step in reverse order. This is the state-machine control over
/// success / failure / compensation that keeps a multi-system operation consistent
/// when it cannot be one database transaction.
/// </summary>
public sealed class SagaRunner<TState>
{
    private readonly ILogger _logger;

    public SagaRunner(ILogger logger) => _logger = logger;

    public async Task<SagaResult> RunAsync(
        TState state,
        IReadOnlyList<ISagaStep<TState>> steps,
        CancellationToken ct = default)
    {
        var completed = new List<ISagaStep<TState>>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            try
            {
                await step.ExecuteAsync(state, ct);
                completed.Add(step);
                _logger.LogDebug("Saga step '{Step}' completed.", step.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saga step '{Step}' failed; compensating {Count} completed step(s).",
                    step.Name, completed.Count);
                return await CompensateAsync(state, completed, step.Name, ex);
            }
        }

        return new SagaResult(
            SagaOutcome.Completed,
            completed.Select(s => s.Name).ToList(),
            CompensatedSteps: Array.Empty<string>());
    }

    private async Task<SagaResult> CompensateAsync(
        TState state,
        List<ISagaStep<TState>> completed,
        string failedStep,
        Exception cause)
    {
        var compensated = new List<string>();
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var step = completed[i];
            try
            {
                await step.CompensateAsync(state, CancellationToken.None);
                compensated.Add(step.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compensation for step '{Step}' failed — manual intervention required.", step.Name);
                return new SagaResult(
                    SagaOutcome.CompensationFailed,
                    completed.Select(s => s.Name).ToList(),
                    compensated,
                    failedStep,
                    $"Compensation failed on '{step.Name}': {ex.Message}");
            }
        }

        return new SagaResult(
            SagaOutcome.Compensated,
            completed.Select(s => s.Name).ToList(),
            compensated,
            failedStep,
            cause.Message);
    }
}

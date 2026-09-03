using FlowForge.Abstractions.Orchestration;
using FlowForge.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlowForge.Tests;

public class SagaRunnerTests
{
    private sealed class TrackingStep : ISagaStep<List<string>>
    {
        private readonly bool _fail;
        public TrackingStep(string name, bool fail = false) { Name = name; _fail = fail; }
        public string Name { get; }

        public Task ExecuteAsync(List<string> log, CancellationToken ct)
        {
            if (_fail) throw new InvalidOperationException($"{Name} failed");
            log.Add($"do:{Name}");
            return Task.CompletedTask;
        }

        public Task CompensateAsync(List<string> log, CancellationToken ct)
        {
            log.Add($"undo:{Name}");
            return Task.CompletedTask;
        }
    }

    private static SagaRunner<List<string>> NewRunner() => new(NullLogger.Instance);

    [Fact]
    public async Task All_steps_succeed_completes_without_compensation()
    {
        var log = new List<string>();
        var steps = new ISagaStep<List<string>>[]
        {
            new TrackingStep("a"), new TrackingStep("b"), new TrackingStep("c")
        };

        var result = await NewRunner().RunAsync(log, steps);

        Assert.Equal(SagaOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "do:a", "do:b", "do:c" }, log);
    }

    [Fact]
    public async Task Failure_compensates_completed_steps_in_reverse()
    {
        var log = new List<string>();
        var steps = new ISagaStep<List<string>>[]
        {
            new TrackingStep("a"),
            new TrackingStep("b"),
            new TrackingStep("c", fail: true)
        };

        var result = await NewRunner().RunAsync(log, steps);

        Assert.Equal(SagaOutcome.Compensated, result.Outcome);
        Assert.Equal("c", result.FailedStep);
        // forward a, b then compensate b, a (reverse); c never completed → not compensated
        Assert.Equal(new[] { "do:a", "do:b", "undo:b", "undo:a" }, log);
    }

    [Fact]
    public async Task Compensation_failure_is_reported()
    {
        var log = new List<string>();
        var steps = new ISagaStep<List<string>>[]
        {
            new ThrowingCompensation("a"),
            new TrackingStep("b", fail: true)
        };

        var result = await NewRunner().RunAsync(log, steps);

        Assert.Equal(SagaOutcome.CompensationFailed, result.Outcome);
    }

    private sealed class ThrowingCompensation : ISagaStep<List<string>>
    {
        public ThrowingCompensation(string name) => Name = name;
        public string Name { get; }
        public Task ExecuteAsync(List<string> log, CancellationToken ct) { log.Add($"do:{Name}"); return Task.CompletedTask; }
        public Task CompensateAsync(List<string> log, CancellationToken ct) => throw new InvalidOperationException("cannot undo");
    }
}

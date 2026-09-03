using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Messaging;
using FlowForge.Abstractions.Orchestration;
using FlowForge.Core.Jobs;
using FlowForge.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace FlowForge.Sample;

/// <summary>Mutable state threaded through the settlement saga.</summary>
public sealed class SettlementState
{
    public SettlementState(string tenantId) => TenantId = tenantId;

    public string TenantId { get; }
    public bool FundsReserved { get; set; }
    public bool LedgerPosted { get; set; }
    public int LineItems { get; set; } = 42;
}

/// <summary>
/// A settlement close that spans three systems that cannot share one transaction:
/// reserve funds → post ledger → issue invoice. It demonstrates saga compensation —
/// if a later step fails, the completed steps are rolled back in reverse order.
///
/// Parameter: failStep = "invoice" injects a failure on the last step to trigger
/// compensation of the first two.
/// </summary>
[JobHandler("settlement.close")]
public sealed class SettlementCloseJob : JobBase
{
    public SettlementCloseJob(ILogger<SettlementCloseJob> logger) : base(logger) { }

    protected override async Task<JobResult> ExecuteCoreAsync(JobContext context)
    {
        var state = new SettlementState(context.TenantId);
        var runner = new SagaRunner<SettlementState>(Logger);

        var steps = new ISagaStep<SettlementState>[]
        {
            new ReserveFundsStep(Logger),
            new PostLedgerStep(Logger),
            new IssueInvoiceStep(Logger, failOnPurpose: context.Param("failStep") == "invoice")
        };

        var result = await runner.RunAsync(state, steps, context.Cancellation);

        return result.Outcome switch
        {
            SagaOutcome.Completed => JobResult.Success(state.LineItems, "settlement closed"),
            SagaOutcome.Compensated => JobResult.Failure($"rolled back at '{result.FailedStep}': {result.Error}"),
            _ => JobResult.Failure($"compensation failed: {result.Error}")
        };
    }
}

internal sealed class ReserveFundsStep : ISagaStep<SettlementState>
{
    private readonly ILogger _logger;
    public ReserveFundsStep(ILogger logger) => _logger = logger;
    public string Name => "reserve-funds";

    public Task ExecuteAsync(SettlementState state, CancellationToken ct)
    {
        state.FundsReserved = true;
        _logger.LogInformation("[{Tenant}] funds reserved.", state.TenantId);
        return Task.CompletedTask;
    }

    public Task CompensateAsync(SettlementState state, CancellationToken ct)
    {
        state.FundsReserved = false;
        _logger.LogInformation("[{Tenant}] funds reservation released (compensation).", state.TenantId);
        return Task.CompletedTask;
    }
}

internal sealed class PostLedgerStep : ISagaStep<SettlementState>
{
    private readonly ILogger _logger;
    public PostLedgerStep(ILogger logger) => _logger = logger;
    public string Name => "post-ledger";

    public Task ExecuteAsync(SettlementState state, CancellationToken ct)
    {
        state.LedgerPosted = true;
        _logger.LogInformation("[{Tenant}] ledger posted.", state.TenantId);
        return Task.CompletedTask;
    }

    public Task CompensateAsync(SettlementState state, CancellationToken ct)
    {
        state.LedgerPosted = false;
        _logger.LogInformation("[{Tenant}] ledger entry reversed (compensation).", state.TenantId);
        return Task.CompletedTask;
    }
}

internal sealed class IssueInvoiceStep : ISagaStep<SettlementState>
{
    private readonly ILogger _logger;
    private readonly bool _failOnPurpose;

    public IssueInvoiceStep(ILogger logger, bool failOnPurpose)
    {
        _logger = logger;
        _failOnPurpose = failOnPurpose;
    }

    public string Name => "issue-invoice";

    public Task ExecuteAsync(SettlementState state, CancellationToken ct)
    {
        if (_failOnPurpose)
            throw new InvalidOperationException("invoice service unavailable");

        _logger.LogInformation("[{Tenant}] invoice issued for {Items} line items.", state.TenantId, state.LineItems);
        return Task.CompletedTask;
    }

    // Nothing to undo if issuing never succeeded.
    public Task CompensateAsync(SettlementState state, CancellationToken ct) => Task.CompletedTask;
}

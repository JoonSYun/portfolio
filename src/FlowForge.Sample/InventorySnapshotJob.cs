using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Messaging;
using FlowForge.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace FlowForge.Sample;

/// <summary>
/// A minimal periodic job. It shows the smallest thing a domain has to write: a
/// validation rule and the core logic. Logging, retry, timeout, idempotency,
/// history and gating are all supplied by the framework around this class.
/// </summary>
[JobHandler("inventory.snapshot")]
public sealed class InventorySnapshotJob : JobBase
{
    public InventorySnapshotJob(ILogger<InventorySnapshotJob> logger) : base(logger) { }

    protected override Task<JobResult?> ValidateAsync(JobContext context)
    {
        if (!context.Parameters.ContainsKey("warehouse"))
            return Task.FromResult<JobResult?>(JobResult.Failure("Missing required parameter 'warehouse'."));
        return Task.FromResult<JobResult?>(null);
    }

    protected override async Task<JobResult> ExecuteCoreAsync(JobContext context)
    {
        var warehouse = context.Param("warehouse");
        await Task.Delay(20, context.Cancellation); // stand-in for real I/O

        // Deterministic stand-in for "number of SKUs snapshotted".
        var skuCount = 100 + warehouse.Length * 7;
        Logger.LogInformation("Captured inventory snapshot for warehouse '{Warehouse}' ({Count} SKUs).",
            warehouse, skuCount);

        return JobResult.Success(skuCount, $"snapshot:{warehouse}");
    }
}

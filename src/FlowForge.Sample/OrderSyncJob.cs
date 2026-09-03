using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Messaging;
using FlowForge.Abstractions.Orchestration;
using FlowForge.Core.Jobs;
using FlowForge.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace FlowForge.Sample;

/// <summary>
/// A batch job that syncs a tenant's orders to an external channel. It demonstrates
/// Job → Chunk → Step: the batch is partitioned into chunks processed in parallel,
/// and a per-item failure is isolated to its step rather than sinking the batch.
///
/// Parameters: batchSize (default 40), chunkSize (default 10), failEvery (default 0
/// = never; set >0 to inject a deterministic per-item failure and see isolation).
/// </summary>
[JobHandler("order.sync")]
public sealed class OrderSyncJob : JobBase
{
    public OrderSyncJob(ILogger<OrderSyncJob> logger) : base(logger) { }

    protected override async Task<JobResult> ExecuteCoreAsync(JobContext context)
    {
        var batchSize = ParseInt(context.Param("batchSize", "40"), 40);
        var chunkSize = ParseInt(context.Param("chunkSize", "10"), 10);
        var failEvery = ParseInt(context.Param("failEvery", "0"), 0);

        var orders = Enumerable.Range(1, batchSize)
            .Select(i => new WorkItem(
                $"{context.TenantId}-ORD-{i:D4}",
                new Dictionary<string, string> { ["seq"] = i.ToString() }))
            .ToList();

        var processor = new ChunkedJobProcessor();
        var chunkResults = await processor.ProcessAsync(
            orders,
            chunkSize,
            maxParallelism: 4,
            processItem: (item, ct) => SyncOrderAsync(item, failEvery, ct),
            context.Cancellation);

        var synced = chunkResults.Sum(c => c.Succeeded);
        var failed = chunkResults.Sum(c => c.Failed);
        var summary = $"{synced} synced / {failed} failed across {chunkResults.Count} chunk(s)";
        Logger.LogInformation("Order sync for {Tenant}: {Summary}.", context.TenantId, summary);

        return failed == 0
            ? JobResult.Success(synced, summary)
            : JobResult.Failure(summary);
    }

    private static async Task SyncOrderAsync(WorkItem order, int failEvery, CancellationToken ct)
    {
        await Task.Delay(2, ct); // stand-in for a channel round-trip
        if (failEvery > 0)
        {
            var seq = int.Parse(order.Payload["seq"]);
            if (seq % failEvery == 0)
                throw new InvalidOperationException($"channel rejected order {order.Id}");
        }
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;
}

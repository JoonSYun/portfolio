using MassTransit;
using Portfolio.WmsOrchestration.BaseFramework;

namespace Portfolio.WmsOrchestration.DistributedOrchestration;

/// <summary>
/// [담당업무 1] Chunk → Step 처리. 청크 하나를 받아 항목별 Step 을 돌리고 결과를 집계한다.
///
/// Step 실패는 항목에 격리된다 — 500건 중 3건이 실패해도 497건은 반영되고, 3건은 실패 목록으로 Saga 에 올라간다.
/// 청크 전체를 처리할 수 없는 경우(대상 시스템 다운 등)에만 ChunkFailed 를 보내 Saga 가 보상을 판단한다.
/// </summary>
[MessageQueue("wms.sync.chunk", PrefetchCount = 4, ConcurrentMessageLimit = 4)]
public sealed class ChunkProcessor : ConsumerBase<ChunkCreated>
{
    private readonly ISyncItemSource _source;
    private readonly ISyncStepExecutor _step;

    public ChunkProcessor(ISyncItemSource source, ISyncStepExecutor step, ConsumerDependencies deps) : base(deps)
    { _source = source; _step = step; }

    protected override string DedupKey(ChunkCreated m) => $"chunk:{m.ChunkId}";

    protected override async Task HandleAsync(ConsumeContext<ChunkCreated> ctx)
    {
        var chunk = ctx.Message;
        IReadOnlyList<SyncItem> items;
        try
        {
            items = await _source.LoadAsync(chunk.ItemKeys, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            await ctx.Publish(new ChunkFailed(chunk.JobId, chunk.ChunkId, $"항목 로드 실패: {ex.Message}"));
            return;
        }

        var failures = new List<StepFailure>();
        var succeeded = 0;

        // Step 병렬도는 청크 내부에서 제한 — 청크 간 병렬은 워커/큐가 담당
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ctx.CancellationToken },
            async (item, ct) =>
            {
                try
                {
                    await _step.ExecuteAsync(item, ct);
                    Interlocked.Increment(ref succeeded);
                }
                catch (TransientStepException ex) { lock (failures) failures.Add(new StepFailure(item.Key, ex.Message, Retryable: true)); }
                catch (Exception ex) { lock (failures) failures.Add(new StepFailure(item.Key, ex.Message, Retryable: false)); }
            });

        await NotifyStatusAsync($"chunk {chunk.ChunkIndex + 1}/{chunk.ChunkCount}: {succeeded} ok / {failures.Count} failed");
        await ctx.Publish(new ChunkCompleted(chunk.JobId, chunk.ChunkId, succeeded, failures.Count, failures));
    }
}

/// <summary>[담당업무 1] 보상 — Saga 가 보상을 결정하면 청크 단위로 되돌린다.</summary>
[MessageQueue("wms.sync.compensate")]
public sealed class ChunkCompensator : ConsumerBase<CompensateChunk>
{
    private readonly ISyncStepExecutor _step;
    public ChunkCompensator(ISyncStepExecutor step, ConsumerDependencies deps) : base(deps) => _step = step;

    protected override string DedupKey(CompensateChunk m) => $"compensate:{m.ChunkId}";

    protected override async Task HandleAsync(ConsumeContext<CompensateChunk> ctx)
    {
        await _step.CompensateChunkAsync(ctx.Message.ChunkId, ctx.CancellationToken);
        await ctx.Publish(new ChunkCompensated(ctx.Message.JobId, ctx.Message.ChunkId));
    }
}

public interface ISyncStepExecutor
{
    Task ExecuteAsync(SyncItem item, CancellationToken ct);
    Task CompensateChunkAsync(Guid chunkId, CancellationToken ct);
}

public sealed class TransientStepException : Exception { public TransientStepException(string m) : base(m) { } }

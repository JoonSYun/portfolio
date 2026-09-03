using FlowForge.Abstractions.Orchestration;

namespace FlowForge.Core.Orchestration;

/// <summary>
/// Splits a large job into Job → Chunk → Step and processes chunks in parallel with
/// bounded concurrency. Failure is isolated per item (a bad row fails its step, not
/// the chunk) and per chunk (a bad chunk does not sink the job), so a partial batch
/// still makes progress.
/// </summary>
public sealed class ChunkedJobProcessor
{
    public static IReadOnlyList<Chunk> Partition(IReadOnlyList<WorkItem> items, int chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var chunks = new List<Chunk>();
        for (var i = 0; i < items.Count; i += chunkSize)
        {
            var slice = items.Skip(i).Take(chunkSize).ToList();
            chunks.Add(new Chunk(chunks.Count, slice));
        }
        return chunks;
    }

    public async Task<IReadOnlyList<ChunkResult>> ProcessAsync(
        IReadOnlyList<WorkItem> items,
        int chunkSize,
        int maxParallelism,
        Func<WorkItem, CancellationToken, Task> processItem,
        CancellationToken ct = default)
    {
        var chunks = Partition(items, chunkSize);
        var results = new ChunkResult[chunks.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallelism));

        var tasks = chunks.Select(async chunk =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[chunk.Index] = await ProcessChunkAsync(chunk, processItem, ct);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<ChunkResult> ProcessChunkAsync(
        Chunk chunk,
        Func<WorkItem, CancellationToken, Task> processItem,
        CancellationToken ct)
    {
        var steps = new List<StepResult>(chunk.Items.Count);
        foreach (var item in chunk.Items)
        {
            try
            {
                await processItem(item, ct);
                steps.Add(new StepResult(item.Id, Success: true));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                steps.Add(new StepResult(item.Id, Success: false, ex.Message));
            }
        }
        return new ChunkResult(chunk.Index, steps);
    }
}

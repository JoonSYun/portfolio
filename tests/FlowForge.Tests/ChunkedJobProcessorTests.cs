using FlowForge.Abstractions.Orchestration;
using FlowForge.Core.Orchestration;
using Xunit;

namespace FlowForge.Tests;

public class ChunkedJobProcessorTests
{
    private static IReadOnlyList<WorkItem> Items(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new WorkItem(i.ToString(), new Dictionary<string, string>()))
            .ToList();

    [Fact]
    public void Partition_splits_into_expected_chunk_sizes()
    {
        var chunks = ChunkedJobProcessor.Partition(Items(25), chunkSize: 10);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(10, chunks[0].Items.Count);
        Assert.Equal(10, chunks[1].Items.Count);
        Assert.Equal(5, chunks[2].Items.Count);
        Assert.Equal(new[] { 0, 1, 2 }, chunks.Select(c => c.Index));
    }

    [Fact]
    public async Task Per_item_failure_is_isolated_and_aggregated()
    {
        var processor = new ChunkedJobProcessor();

        var results = await processor.ProcessAsync(
            Items(20),
            chunkSize: 5,
            maxParallelism: 3,
            processItem: (item, ct) =>
            {
                // fail every 4th item; the rest still succeed
                if (int.Parse(item.Id) % 4 == 0)
                    throw new InvalidOperationException("bad item");
                return Task.CompletedTask;
            });

        var succeeded = results.Sum(r => r.Succeeded);
        var failed = results.Sum(r => r.Failed);

        Assert.Equal(15, succeeded);
        Assert.Equal(5, failed);
        Assert.All(results, r => Assert.Equal(5, r.Steps.Count)); // every item accounted for
    }
}

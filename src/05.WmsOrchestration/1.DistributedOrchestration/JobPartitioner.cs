using MassTransit;

namespace Portfolio.WmsOrchestration.DistributedOrchestration;

/// <summary>
/// [담당업무 1] Job → Chunk 분할. Saga 가 Job 을 받으면 이 컨슈머가 청크로 잘라 뿌린다.
/// 청크 크기는 대상 시스템의 API 배치 한도와 워커 메모리로 정한다 (쇼핑몰 연동은 500).
/// 청크는 서로 독립이므로 워커 수만큼 병렬로 처리된다.
/// </summary>
public sealed class JobPartitioner : IConsumer<BulkSyncJobSubmitted>
{
    private const int ChunkSize = 500;
    private readonly ISyncItemSource _source;

    public JobPartitioner(ISyncItemSource source) => _source = source;

    public async Task Consume(ConsumeContext<BulkSyncJobSubmitted> ctx)
    {
        var job = ctx.Message;
        var keys = await _source.ListItemKeysAsync(job.TenantId, job.SyncType, job.TargetDate, ctx.CancellationToken);

        var chunks = keys.Chunk(ChunkSize).ToList();
        for (var i = 0; i < chunks.Count; i++)
        {
            await ctx.Publish(new ChunkCreated(job.JobId, NewId.NextGuid(), i, chunks.Count, chunks[i]), ctx.CancellationToken);
        }
    }
}

public interface ISyncItemSource
{
    Task<IReadOnlyList<string>> ListItemKeysAsync(string tenant, string syncType, DateOnly date, CancellationToken ct);
    Task<IReadOnlyList<SyncItem>> LoadAsync(IReadOnlyList<string> keys, CancellationToken ct);
}

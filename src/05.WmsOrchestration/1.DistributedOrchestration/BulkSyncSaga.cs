using MassTransit;

namespace Portfolio.WmsOrchestration.DistributedOrchestration;

/// <summary>Saga 인스턴스 상태 — DB(EF Core) 에 영속. 워커가 죽어도 Job 의 진행 상태는 남는다.</summary>
public sealed class BulkSyncState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }          // = JobId
    public string CurrentState { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public int ChunkCount { get; set; }
    public int ChunksDone { get; set; }
    public int ChunksCompensated { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string? FailureReason { get; set; }
    public List<Guid> CompletedChunkIds { get; set; } = new();
    public DateTime SubmittedAt { get; set; }
    public byte[] RowVersion { get; set; } = default!; // 낙관적 동시성 — 청크 완료가 동시에 들어와도 안전
}

/// <summary>
/// [담당업무 1] Saga State Machine — 각 단계의 성공·실패·보상 흐름을 자동 제어.
///
///   Submitted ──JobSubmitted──▶ Partitioning ──ChunkCreated(첫)──▶ Processing
///   Processing ──ChunkCompleted(전부)──▶ Completed
///   Processing ──ChunkFailed──▶ Compensating ──ChunkCompensated(전부)──▶ Failed
///
/// 상태 전이 조건과 보상 트리거를 코드로 선언하므로, "청크 7개 중 5개 끝난 상태에서 하나가 죽으면 어떻게 되나"
/// 같은 질문에 답이 항상 같다: 완료된 5개를 보상하고 Job 을 Failed 로 마감한다.
/// </summary>
public sealed class BulkSyncSaga : MassTransitStateMachine<BulkSyncState>
{
    public State Partitioning { get; private set; } = default!;
    public State Processing { get; private set; } = default!;
    public State Compensating { get; private set; } = default!;
    public State Completed { get; private set; } = default!;
    public State Failed { get; private set; } = default!;

    public Event<BulkSyncJobSubmitted> JobSubmitted { get; private set; } = default!;
    public Event<ChunkCreated> ChunkCreated { get; private set; } = default!;
    public Event<ChunkCompleted> ChunkCompleted { get; private set; } = default!;
    public Event<ChunkFailed> ChunkFailed { get; private set; } = default!;
    public Event<ChunkCompensated> ChunkCompensated { get; private set; } = default!;

    public BulkSyncSaga()
    {
        InstanceState(x => x.CurrentState);

        Event(() => JobSubmitted, x => x.CorrelateById(m => m.Message.JobId));
        Event(() => ChunkCreated, x => x.CorrelateById(m => m.Message.JobId));
        Event(() => ChunkCompleted, x => x.CorrelateById(m => m.Message.JobId));
        Event(() => ChunkFailed, x => x.CorrelateById(m => m.Message.JobId));
        Event(() => ChunkCompensated, x => x.CorrelateById(m => m.Message.JobId));

        Initially(
            When(JobSubmitted)
                .Then(ctx => { ctx.Saga.TenantId = ctx.Message.TenantId; ctx.Saga.SubmittedAt = DateTime.UtcNow; })
                .TransitionTo(Partitioning));

        During(Partitioning, Processing,
            When(ChunkCreated)
                .Then(ctx => ctx.Saga.ChunkCount = ctx.Message.ChunkCount)
                .TransitionTo(Processing));

        During(Processing,
            When(ChunkCompleted)
                .Then(ctx =>
                {
                    ctx.Saga.ChunksDone++;
                    ctx.Saga.Succeeded += ctx.Message.Succeeded;
                    ctx.Saga.Failed += ctx.Message.Failed;
                    ctx.Saga.CompletedChunkIds.Add(ctx.Message.ChunkId);
                })
                .If(ctx => ctx.Saga.ChunksDone == ctx.Saga.ChunkCount, x => x
                    .PublishAsync(ctx => ctx.Init<BulkSyncJobCompleted>(new { JobId = ctx.Saga.CorrelationId, ctx.Saga.Succeeded, ctx.Saga.Failed }))
                    .TransitionTo(Completed)
                    .Finalize()),

            // 청크 하나라도 처리 불가 → 이미 반영된 청크를 전부 되돌린다
            When(ChunkFailed)
                .Then(ctx => ctx.Saga.FailureReason = ctx.Message.Reason)
                .IfElse(ctx => ctx.Saga.CompletedChunkIds.Count == 0,
                    nothingToUndo => nothingToUndo
                        .PublishAsync(ctx => ctx.Init<BulkSyncJobCompensated>(new { JobId = ctx.Saga.CorrelationId, Reason = ctx.Saga.FailureReason }))
                        .TransitionTo(Failed).Finalize(),
                    undo => undo
                        .ThenAsync(async ctx =>
                        {
                            foreach (var chunkId in ctx.Saga.CompletedChunkIds)
                                await ctx.Publish(new CompensateChunk(ctx.Saga.CorrelationId, chunkId));
                        })
                        .TransitionTo(Compensating)));

        During(Compensating,
            When(ChunkCompensated)
                .Then(ctx => ctx.Saga.ChunksCompensated++)
                .If(ctx => ctx.Saga.ChunksCompensated == ctx.Saga.CompletedChunkIds.Count, x => x
                    .PublishAsync(ctx => ctx.Init<BulkSyncJobCompensated>(new { JobId = ctx.Saga.CorrelationId, Reason = ctx.Saga.FailureReason }))
                    .TransitionTo(Failed)
                    .Finalize()),

            Ignore(ChunkCompleted),   // 보상 중 뒤늦게 도착한 완료는 무시 — 이미 되돌리는 중
            Ignore(ChunkFailed));

        SetCompletedWhenFinalized();
    }
}

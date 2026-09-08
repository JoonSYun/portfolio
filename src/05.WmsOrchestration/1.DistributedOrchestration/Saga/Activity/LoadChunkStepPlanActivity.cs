using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Saga;
using System.Text;

public class LoadChunkStepPlanActivity : IStateMachineActivity<ChunkStepSagaState, ChunkStepInitEvent>
{
    private readonly ILogger<LoadChunkStepPlanActivity> _logger;
    private readonly ChunkStepQueryRepository _chunkStepQueryRepository;
    private readonly ChunkPayloadQueryRepository _chunkPayloadQueryRepository;
    private readonly OrchestrationRethriveRetryPolicyFactory _retryPolicyFactory;

    public LoadChunkStepPlanActivity(
        ILogger<LoadChunkStepPlanActivity> logger,
        ChunkStepQueryRepository repo,
        ChunkPayloadQueryRepository chunkPayloadQueryRepository,
        OrchestrationRethriveRetryPolicyFactory retryPolicyFactory
        )
    {
        _logger = logger;
        _chunkStepQueryRepository = repo;
        _chunkPayloadQueryRepository = chunkPayloadQueryRepository;
        _retryPolicyFactory = retryPolicyFactory;
    }

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public async Task Execute(
        BehaviorContext<ChunkStepSagaState, ChunkStepInitEvent> context,
        IBehavior<ChunkStepSagaState, ChunkStepInitEvent> next)
    {
        var saga = context.Saga;
        var msg = context.Message;

        try
        {
            var policy = _retryPolicyFactory.CreateRetryPolicy();

            // 병렬 조회 + Retry 적용
            var (steps, payload) = await policy.ExecuteAsync(async () =>
            {
                var stepsTask = _chunkStepQueryRepository.GetStepsByChunkIdAsync(msg.ChunkId);
                var payloadTask = _chunkPayloadQueryRepository.GetPayloadByChunkIdAsync(msg.ChunkId);

                await Task.WhenAll(stepsTask, payloadTask);

                return (await stepsTask, await payloadTask);
            });

            // Payload 검증 - 예외 던짐 (필수 데이터)
            if (payload == null || payload.Payload == null || payload.Payload.Length == 0)
            {
                _logger.LogError(
                    AppLog.Log("[LoadChunkStepPlanActivity] Payload not found. ChunkId={ChunkId}"),
                    msg.ChunkId);

                throw new InvalidOperationException($"Payload not found for ChunkId: {msg.ChunkId}");
            }

            // Step 검증 - 예외 안 던짐, 0개면 TotalStepCount=0 설정
            if (!steps.Any())
            {
                _logger.LogWarning(
                    AppLog.Log("[LoadChunkStepPlanActivity] No steps defined. ChunkId={ChunkId}"),
                    msg.ChunkId);

                saga.TotalStepCount = 0;
                saga.StepPlanJson = "[]";
                saga.PayloadJson = Encoding.UTF8.GetString(payload.Payload);

                await next.Execute(context);
                return;
            }

            // DB 값을 그대로 사용 (Build에서 이미 0-based로 정규화됨)
            var plan = steps
                .OrderBy(s => s.StepIndex)
                .Select(s => new ChunkStepPlanInfo
                {
                    Index = s.StepIndex,  // ⭐ DB 값 그대로 사용
                    StepType = s.StepType,
                })
                .ToList();

            saga.TotalStepCount = plan.Count;
            saga.StepPlanJson = System.Text.Json.JsonSerializer.Serialize(plan);
            saga.PayloadJson = Encoding.UTF8.GetString(payload.Payload);

            _logger.LogInformation(
                AppLog.Log("[LoadChunkStepPlanActivity] Loaded plan and payload. ChunkId={ChunkId}, Steps={StepCount}, PayloadSize={Size}"),
                saga.ChunkId, saga.TotalStepCount, payload.Payload.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                AppLog.Log("[LoadChunkStepPlanActivity] Failed to load plan or payload. ChunkId={ChunkId}"),
                msg.ChunkId);
            throw;
        }

        await next.Execute(context);
    }

    public Task Faulted<TException>(
        BehaviorExceptionContext<ChunkStepSagaState, ChunkStepInitEvent, TException> context,
        IBehavior<ChunkStepSagaState, ChunkStepInitEvent> next)
        where TException : Exception
        => next.Faulted(context);

    public void Probe(ProbeContext context)
    {
        context.CreateScope("load-chunk-step-plan");
    }
}
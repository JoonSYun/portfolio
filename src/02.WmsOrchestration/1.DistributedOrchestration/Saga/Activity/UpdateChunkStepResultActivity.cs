using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Saga;

public class UpdateChunkStepResultActivity
    : IStateMachineActivity<ChunkStepSagaState, ChunkStepResultEvent>
{
    private readonly ILogger<UpdateChunkStepResultActivity> _logger;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ChunkStepCommandRepository _repo;
    private readonly OrchestrationStatusRetryPolicyFactory _retryPolicyFactory;

    public UpdateChunkStepResultActivity(
        ILogger<UpdateChunkStepResultActivity> logger,
        IUnitOfWork unitOfWork,
        ChunkStepCommandRepository repo,
        OrchestrationStatusRetryPolicyFactory retryPolicyFactory
        )
    {
        _logger = logger;
        _unitOfWork = unitOfWork;
        _repo = repo;
        _retryPolicyFactory = retryPolicyFactory;
    }

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public async Task Execute(
        BehaviorContext<ChunkStepSagaState, ChunkStepResultEvent> context,
        IBehavior<ChunkStepSagaState, ChunkStepResultEvent> next)
    {
        var saga = context.Saga;
        var msg = context.Message;

        var policy = _retryPolicyFactory.CreateRetryPolicy();

        await policy.ExecuteAsync(async () =>
        {
            _repo.UpdateStatus(msg.ChunkId, msg.StepIndex, msg.Status);
            await _unitOfWork.SaveChangesAsync();
        });

        await next.Execute(context);
    }

    public Task Faulted<TException>(
        BehaviorExceptionContext<ChunkStepSagaState, ChunkStepResultEvent, TException> context,
        IBehavior<ChunkStepSagaState, ChunkStepResultEvent> next)
        where TException : Exception
        => next.Faulted(context);

    public void Probe(ProbeContext context)
        => context.CreateScope("update-chunk-step-result");
}
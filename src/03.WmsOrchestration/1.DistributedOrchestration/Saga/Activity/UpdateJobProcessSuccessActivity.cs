using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Repository;

namespace Portfolio.WmsOrchestration.Saga
{
    public class UpdateJobProcessSuccessActivity : IStateMachineActivity<JobProcessSagaState, ProcessCompleteEvent>
    {
        private readonly ILogger<UpdateJobProcessSuccessActivity> _logger;
        private readonly IUnitOfWork _unitOfWork;
        private readonly JobProcessCommandRepository _repo;
        private readonly OrchestrationStatusRetryPolicyFactory _retryPolicyFactory;

        public UpdateJobProcessSuccessActivity(
            ILogger<UpdateJobProcessSuccessActivity> logger,
            IUnitOfWork unitOfWork,
            JobProcessCommandRepository repo,
            OrchestrationStatusRetryPolicyFactory retryPolicyFactory
            )
        {
            _logger = logger;
            _unitOfWork = unitOfWork;
            _repo = repo;
            _retryPolicyFactory = retryPolicyFactory;
        }

        public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);


        public async Task Execute(BehaviorContext<JobProcessSagaState, ProcessCompleteEvent> context, IBehavior<JobProcessSagaState, ProcessCompleteEvent> next)
        {
            var saga = context.Saga;
            var msg = context.Message;

            var policy = _retryPolicyFactory.CreateRetryPolicy();

            await policy.ExecuteAsync(async () =>
            {
                _repo.UpdateStatus(msg.JobId, msg.ProcessIndex, msg.Status);
                await _unitOfWork.SaveChangesAsync();
            });

            await next.Execute(context);
        }

        public Task Faulted<TException>(
            BehaviorExceptionContext<JobProcessSagaState, ProcessCompleteEvent, TException> context, 
            IBehavior<JobProcessSagaState, ProcessCompleteEvent> next) 
            where TException : Exception
            => next.Faulted(context);


        public void Probe(ProbeContext context) => context.CreateScope("update-job-process-status");
    }
}

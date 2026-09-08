using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Saga
{
    public class JobCompleteUpdateCommandConsumer
        : JobConsumerBase<JobCompleteUpdateCommand>
    {
        private readonly JobResultService _jobResultService;

        public JobCompleteUpdateCommandConsumer(
            ILogger<JobCompleteUpdateCommandConsumer> logger,
            JobIdempotencyService jobIdempotencyService,
            JobResultService jobResultService
            )
            : base(logger, jobIdempotencyService)
        {
            _jobResultService = jobResultService;
        }

        protected override async Task ExecuteAsync(JobCompleteUpdateCommand command)
        {
            await _jobResultService.ExecuteJobComplete(command);
        }

        protected override async Task<IdempotencyResult> CheckIdempotencyAsync(ConsumeContext<JobCompleteUpdateCommand> context)
        {
            return await JobIdempotencyService.CheckJobCompleteIdempotency(context.Message.JobId);
        }

        protected override void LogPreExecution(JobCompleteUpdateCommand command)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Received updated (JOB COMPLETE) : JobId={JobId}"),
                GetType().Name, command.JobId);
        }

        protected override void LogPostExecution(JobCompleteUpdateCommand command)
        {
            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] Complete update (JOB COMPLETE) : JobId={JobId}"),
                GetType().Name, command.JobId);
        }

        protected override void LogError(Exception ex, JobCompleteUpdateCommand command)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] ERROR update (JOB COMPLETE) : JobId={JobId}"),
                GetType().Name, command.JobId);
        }
    }
}
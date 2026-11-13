using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Saga
{
    public class JobSuspendedUpdateCommandConsumer
        : JobConsumerBase<JobSuspendedUpdateCommand>
    {
        private readonly JobSuspenseService _jobSuspenseService;

        public JobSuspendedUpdateCommandConsumer(
            ILogger<JobSuspendedUpdateCommandConsumer> logger,
            JobIdempotencyService jobIdempotencyService,
            JobSuspenseService jobSuspenseService
            )
            : base(logger, jobIdempotencyService)
        {
            _jobSuspenseService = jobSuspenseService;
        }

        protected override async Task ExecuteAsync(JobSuspendedUpdateCommand command)
        {
            await _jobSuspenseService.ExecuteJobSuspense(command);
        }

        protected override Task<IdempotencyResult> CheckIdempotencyAsync(ConsumeContext<JobSuspendedUpdateCommand> context)
        {
            return JobIdempotencyService.CheckJobSuspeseIdempotency(context.Message.JobId);
        }

        protected override void LogPreExecution(JobSuspendedUpdateCommand command)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Received update (JOB SUSPENSE) : JobId={JobId}, SuspenseType={SuspenseType}"),
                GetType().Name, command.JobId, command.SuspenseType);
        }

        protected override void LogPostExecution(JobSuspendedUpdateCommand command)
        {
            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] Complete update (JOB SUSPENSE) : JobId={JobId}, SuspenseType={SuspenseType}"),
                GetType().Name, command.JobId, command.SuspenseType);
        }

        protected override void LogError(Exception ex, JobSuspendedUpdateCommand command)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] ERROR update (JOB SUSPENSE) : JobId={JobId}, SuspenseType={SuspenseType}"),
                GetType().Name, command.JobId, command.SuspenseType);
        }
    }
}
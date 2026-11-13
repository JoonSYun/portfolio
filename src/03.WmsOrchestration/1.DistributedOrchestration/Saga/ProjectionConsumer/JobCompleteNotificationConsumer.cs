using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Saga
{
    public class JobCompleteNotificationConsumer : JobConsumerBase<JobCompleteUpdateCommand>
    {
        private readonly JobNotificationService _notificationService;

        public JobCompleteNotificationConsumer(
            ILogger<JobConsumerBase<JobCompleteUpdateCommand>> logger, 
            JobIdempotencyService jobIdempotencyService,
            JobNotificationService jobNotificationService
            ) 
            : base(logger, jobIdempotencyService)
        {
            _notificationService = jobNotificationService;
        }

        protected override async Task ExecuteAsync(JobCompleteUpdateCommand command)
        {
            await _notificationService.ExecuteAsync(command);
        }
    }
}

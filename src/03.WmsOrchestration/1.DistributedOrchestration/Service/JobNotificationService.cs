using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Service
{
    public class JobNotificationService
    {
        private readonly ILogger<JobNotificationService> _logger;
        private readonly NotificationService _notificationService;
        private readonly ChunkFailQueryRepository _chunkFailQueryRepository;

        public JobNotificationService(
            ILogger<JobNotificationService> logger,
            NotificationService notificationService,
            ChunkFailQueryRepository chunkFailQueryRepository
            )
        {
            _logger = logger;
            _notificationService = notificationService;
            _chunkFailQueryRepository = chunkFailQueryRepository;
        }

        public async Task ExecuteAsync(JobCompleteUpdateCommand command)
        {
            try
            {
                List<GetJobError_Query>? errors = null;
                JobCompleteNotification notification = new JobCompleteNotification
                {
                    UserId = command.UserId,
                    JobId = command.JobId,
                    JobIdentifier = command.JobIdentifier,
                    DomainType = command.DomainType,
                    RequestBrandCode = command.RequestBrandCode,
                    TotalChunkCount = command.TotalChunkCount,
                    SlipDiv = command.SlipDiv,
                    Delimiter = command.Delimiter,
                    IsSuccess = command.Status,
                    IsBypass = command.IsBypass,
                    Message = null,
                    ResponsePayload = null
                };

                if (command.Status != ChunkScopeStatus.COMPLETED.ToString())
                {
                    errors = await _chunkFailQueryRepository.GetJobErrors(command.JobId);

                    notification.Message = string.Join(OrchestrationConstants.COMPOSITE_KEY_SEPARATOR, errors.Select(e => e.ErrorMessage));
                    notification.ResponsePayload = errors;

                    await _notificationService.SendToRolesAsync(notification, UserRoleType.ADMIN, UserRoleType.DEVELOPER);
                }
                
                await _notificationService.SendAsync(notification);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[JobNotificationService] Failed to execute job notification: JobId={JobId}"),
                    command.JobId
                );
                throw;
            }
        }
    }
}

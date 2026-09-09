using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Service
{
    public class JobResultService
    {
        private readonly ILogger<JobResultService> _logger;
        private readonly IUnitOfWork _unitOfWork;
        private readonly JobMasterCommandRepository _jobMasterCommandRepository;
        private readonly OrchestrationStatusRetryPolicyFactory _statusRetryPolicyFactory;

        public JobResultService(
            ILogger<JobResultService> logger,
            IUnitOfWork unitOfWork,
            JobMasterCommandRepository jobMasterCommandRepository,
            OrchestrationStatusRetryPolicyFactory statusRetryPolicyFactory
            )
        {
            _logger = logger;
            _unitOfWork = unitOfWork;
            _jobMasterCommandRepository = jobMasterCommandRepository;
            _statusRetryPolicyFactory = statusRetryPolicyFactory;
        }

        #region === Public: Job Complete Handling ===

        public async Task ExecuteJobComplete(JobCompleteUpdateCommand command)
        {
            var policy = _statusRetryPolicyFactory.CreateRetryPolicy();

            await policy.ExecuteAsync(async () =>
            {
                try
                {
                    _logger.LogDebug(
                        AppLog.Log("[JobResultService] All chunks completed for JobId={JobId}. Starting final aggregation."),
                        command.JobId);

                    UpdateJobMasterWithAggregationAsync(command);
                    await _unitOfWork.SaveChangesAsync();

                    _logger.LogInformation(
                        AppLog.Log("[JobResultService] JOB COMPLETED. JobId={JobId}"),
                        command.JobId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        AppLog.Log("[JobResultService] ERROR during job completion aggregation. JobId={JobId}"),
                        command.JobId);
                    _unitOfWork.Rollback();
                    throw;
                }
            });
        }

        #endregion

        #region === Private: Job Completion Logic ===

        private void UpdateJobMasterWithAggregationAsync(JobCompleteUpdateCommand command)
        {
            string resultMessage = StatusHelper.GetJobStatusMessage(command.Status);

            _jobMasterCommandRepository.UpdateJobMasterResult(
                new UpdateJobMasterResult_Command
                {
                    JobId = command.JobId,
                    CompletedCount = command.ChunkSuccessCount + command.ChunkPartialSuccessCount + command.ChunkFailedCount,
                    FailedCount = command.ChunkFailedCount,
                    Status = command.Status,
                    CompletedAt = DateTime.UtcNow,
                    ErrorMessage = resultMessage
                });
        }

        #endregion 
    }
}
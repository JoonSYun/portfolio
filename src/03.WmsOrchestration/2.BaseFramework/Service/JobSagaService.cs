using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;

namespace Portfolio.WmsOrchestration.Service
{
    public class JobSagaService
    {
        private readonly ILogger<JobSagaService> _logger;
        private readonly JobSagaQueryRepository _jobSagaQueryRepository;

        public JobSagaService(ILogger<JobSagaService> logger, JobSagaQueryRepository jobSagaQueryRepository)
        {
            _logger = logger;
            _jobSagaQueryRepository = jobSagaQueryRepository;
        }

        public async Task<bool> CheckJobSagaExistAsync(Guid jobId)
        {
            try
            {
                _logger.LogDebug(
                    AppLog.Log("[JobSagaService] Checking if Job exists: JobId={JobId}"),
                    jobId);
                var count = await _jobSagaQueryRepository.ExistAsync(jobId);
                return count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[JobSagaService] Error in IsJobExistAsync: JobId={JobId}"),
                    jobId);
                throw;
            }
        }

        public async Task<bool> CheckJobSagaEnableAsync(Guid jobId)
        {
            bool valid = false;

            try
            {
                _logger.LogDebug(
                    AppLog.Log("[JobSagaService] Checking if Job exists and status is 'Completed': JobId={JobId}"),
                    jobId);

                var exists = await _jobSagaQueryRepository.ExistAsync(jobId);

                if (exists <= 0)
                {
                    valid = false;
                }

                var status = await _jobSagaQueryRepository.GetColumnValue<string>(jobId, "CurrentState");

                if (status == JobSagaStatus.Completed.ToString())
                {
                    valid = false;
                }
                // Only Exists and Processing status are valid
                else if (status == JobSagaStatus.Processing.ToString())
                {
                    valid = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[JobSagaService] Error in CheckJobSagaExistAndStatusAsync: JobId={JobId}"),
                    jobId);
                throw;
            }

            return valid;
        }
    }
}

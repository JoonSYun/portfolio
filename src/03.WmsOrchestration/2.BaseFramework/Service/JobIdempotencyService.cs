using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;

namespace Portfolio.WmsOrchestration.Service
{
    public class JobIdempotencyService
    {
        private readonly ILogger<JobIdempotencyService> _logger;
        private readonly JobMasterQueryRepository _jobMasterQueryRepository;

        public JobIdempotencyService(
            ILogger<JobIdempotencyService> logger,
            JobMasterQueryRepository jobMasterQueryRepository)
        {
            _logger = logger;
            _jobMasterQueryRepository = jobMasterQueryRepository;
        }

        public async Task<IdempotencyResult> CheckJobPublishIdempotency(Guid jobId)
        {
            string? status = await GetJobStatusAsync(jobId);

            if (status != null && status == JobScopeStatus.PENDING.ToString())
            {
                return IdempotencyResult.NONE;
            }
            else if (status == null)
            {
                throw new Exception($"Missing Jobid : {jobId}");
            }
            else
            {
                return IdempotencyResult.DUPLICATE;
            }
        }

        public async Task<IdempotencyResult> CheckJobCompleteIdempotency(Guid jobId)
        {
            string? status = await GetJobStatusAsync(jobId);

            if (status != null && (status == JobScopeStatus.PENDING.ToString() || status == JobScopeStatus.PROCESSING.ToString()))
            {
                return IdempotencyResult.NONE;
            }
            else if (status == null)
            {
                throw new Exception($"Missing Jobid : {jobId}");
            }
            else
            {
                return IdempotencyResult.DUPLICATE;
            }
        }

        public async Task<IdempotencyResult> CheckJobSuspeseIdempotency(Guid jobId)
        {
            string? status = await GetJobStatusAsync(jobId);

            if (status != null && (status == JobScopeStatus.PENDING.ToString() || status == JobScopeStatus.PROCESSING.ToString()))
            {
                return IdempotencyResult.NONE;
            }
            else if (status == null)
            {
                throw new Exception($"Missing Jobid : {jobId}");
            }
            else
            {
                return IdempotencyResult.DUPLICATE;
            }
        }

        private async Task<string?> GetJobStatusAsync(Guid jobId)
        {
            return await _jobMasterQueryRepository.GetColumnValue<string>(jobId, nameof(JobMaster_Entity.Status));
        }
    }
}

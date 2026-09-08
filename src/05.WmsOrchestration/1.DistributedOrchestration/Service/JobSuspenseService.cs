using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Service
{
    public class JobSuspenseService
    {
        private readonly ILogger<JobSuspenseService> _logger;
        private readonly IUnitOfWork _unitOfWork;
        private readonly JobSuspenseQueryRepository _jobSuspenseQueryRepository;
        private readonly JobSuspenseRedisManager _jobSuspenseRedisManager;
        private readonly JobSuspenseCommandRepository _jobSuspenseCommandRepository;
        private readonly JobMasterCommandRepository _jobMasterCommandRepository;
        private readonly OrchestrationStatusRetryPolicyFactory _statusRetryPolicyFactory;
        private readonly OrchestrationRethriveRetryPolicyFactory _rethriveRetryPolicyFactory;
        private readonly RedisRetryPolicyFactory _redisRetryPolicyFactory;

        public JobSuspenseService(
            ILogger<JobSuspenseService> logger,
            IUnitOfWork unitOfWork,
            JobSuspenseQueryRepository jobSuspenseQueryRepository,
            JobSuspenseRedisManager jobSuspenseRedisManager,
            JobSuspenseCommandRepository jobSuspenseCommandRepository,
            JobMasterCommandRepository jobMasterCommandRepository,
            OrchestrationStatusRetryPolicyFactory statusRetryPolicyFactory,
            OrchestrationRethriveRetryPolicyFactory rethriveRetryPolicyFactory,
            RedisRetryPolicyFactory redisRetryPolicyFactory
            )
        {
            _logger = logger;
            _unitOfWork = unitOfWork;
            _jobSuspenseQueryRepository = jobSuspenseQueryRepository;
            _jobSuspenseRedisManager = jobSuspenseRedisManager;
            _jobSuspenseCommandRepository = jobSuspenseCommandRepository;
            _jobMasterCommandRepository = jobMasterCommandRepository;
            _statusRetryPolicyFactory = statusRetryPolicyFactory;
            _rethriveRetryPolicyFactory = rethriveRetryPolicyFactory;
            _redisRetryPolicyFactory = redisRetryPolicyFactory;
        }

        /// <summary>
        /// Checks whether the specified job is in suspense state.
        /// when a job is suspended, return true.
        /// </summary>
        /// <param name="jobId"></param>
        /// <returns></returns>
        public async Task<bool> DoesJobSuspense(Guid jobId)
        {
            try
            {
                bool exists = false;
                var redisPolicy = _redisRetryPolicyFactory.CreateRetryPolicy();

                try
                {
                    exists = await redisPolicy.ExecuteAsync(async () =>
                        await _jobSuspenseRedisManager.ExistsAsync(jobId));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        AppLog.Log("[JobSuspenseService] Redis job suspense check failed: JobId={JobId}"),
                        jobId);

                    var rethrivePolicy = _rethriveRetryPolicyFactory.CreateRetryPolicy();
                    exists = await rethrivePolicy.ExecuteAsync(async () =>
                        await _jobSuspenseQueryRepository.IsExist(jobId));
                }

                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[JobSuspenseService] Error occurred while checking job suspense: JobId={JobId}"),
                    jobId);
                throw;
            }
        }

        public async Task ExecuteJobSuspense(JobSuspendedUpdateCommand command)
        {
            var statusPolicy = _statusRetryPolicyFactory.CreateRetryPolicy();
            var redisPolicy = _redisRetryPolicyFactory.CreateRetryPolicy();

            await statusPolicy.ExecuteAsync(async () =>
            {
                var suspenseCommand = new CreateJobSuspense_Command
                {
                    JobId = command.JobId,
                    IdempotencyKey = command.IdempotencyKey,
                    SuspenseType = command.SuspenseType,
                    CreatedBy = command.CreatedBy,
                    SuspenseReason = command.SuspenseReason
                };

                var status = command.SuspenseType == JobSuspenseType.CANCELED.ToString()
                    ? JobScopeStatus.CANCELED
                    : JobScopeStatus.SUSPENDED;

                await redisPolicy.ExecuteAsync(async () =>
                    await _jobSuspenseRedisManager.AddAsync(command.JobId));

                _jobSuspenseCommandRepository.CreateJobSuspense(suspenseCommand);
                _jobMasterCommandRepository.Update(command.JobId, nameof(JobMaster_Entity.Status), status.ToString());
                await _unitOfWork.SaveChangesAsync();
            });
        }
    }
}
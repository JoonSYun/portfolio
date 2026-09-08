using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Service
{
    public class ChunkResultService
    {
        #region === Fields ===

        private readonly ILogger<ChunkResultService> _logger;
        private readonly IUnitOfWork _unitOfWork;
        private readonly JobCompleteChunkRedisManager _jobCompleteChunkManager;
        private readonly JobMasterCommandRepository _jobMasterCommandRepository;
        private readonly ChunkMasterCommandRepository _chunkMasterCommandRepository;
        private readonly ChunkMasterQueryRepository _chunkMasterQueryRespository;
        private readonly ChunkFailCommandRepository _chunkFailCommandRepository;
        private readonly OrchestrationStatusRetryPolicyFactory _statusRetryPolicyFactory;
        private readonly RedisRetryPolicyFactory _redisRetryPolicyFactory;

        #endregion

        #region === Constructor ===

        public ChunkResultService(
            ILogger<ChunkResultService> logger,
            IUnitOfWork unitOfWork,
            JobCompleteChunkRedisManager jobCompleteChunkManager,
            JobMasterCommandRepository jobMasterCommandRepository,
            ChunkMasterCommandRepository chunkMasterCommandRepository,
            ChunkMasterQueryRepository chunkMasterQueryRespository,
            ChunkFailCommandRepository chunkFailCommandRepository,
            PublishService ntPublishService,
            OrchestrationStatusRetryPolicyFactory statusRetryPolicyFactory,
            RedisRetryPolicyFactory redisRetryPolicyFactory
            )
        {
            _logger = logger;
            _unitOfWork = unitOfWork;
            _jobCompleteChunkManager = jobCompleteChunkManager;
            _jobMasterCommandRepository = jobMasterCommandRepository;
            _chunkMasterCommandRepository = chunkMasterCommandRepository;
            _chunkMasterQueryRespository = chunkMasterQueryRespository;
            _chunkFailCommandRepository = chunkFailCommandRepository;
            _statusRetryPolicyFactory = statusRetryPolicyFactory;
            _redisRetryPolicyFactory = redisRetryPolicyFactory;
        }

        #endregion

        #region === Public: Chunk Result Handling (Unified) ===

        public async Task<AllChunkResult_DTO> ExecuteChunkResult(ChunkResultUpdateCommand message)
        {
            var result = new AllChunkResult_DTO();

            try
            {
                _logger.LogDebug(
                    AppLog.Log("[ChunkResultService] CHUNK RESULT RECEIVED. ChunkId={ChunkId}, JobId={JobId}, Status={Status}"),
                    message.ChunkId, message.JobId, message.Status);

                // Route by Status
                if (message.Status == ChunkScopeStatus.COMPLETED.ToString())
                {
                    await UpdateChunkSuccessAsync(message);
                }
                else if (message.Status == ChunkScopeStatus.PARTIAL_SUCCESS.ToString())
                {
                    await UpdateChunkPartialSuccessAsync(message);
                }
                else if (message.Status == ChunkScopeStatus.FAILED.ToString())
                {
                    await UpdateChunkFailAsync(message);
                }
                else
                {
                    _logger.LogError(
                        AppLog.Log("[ChunkResultService] Unknown chunk status. ChunkId={ChunkId}, Status={Status}"),
                        message.ChunkId, message.Status);
                    throw new InvalidOperationException($"Unknown chunk status: {message.Status}");
                }

                return await CheckAllChunkComplete(message, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[ChunkResultService] Error while handling chunk result. ChunkId={ChunkId}, JobId={JobId}, Status={Status}"),
                    message.ChunkId, message.JobId, message.Status);
                throw;
            }
            finally
            {
                _unitOfWork.Dispose();
            }
        }

        #endregion

        #region === Private: Chunk Operations ===

        /// <summary>
        /// Handle chunk success (COMPLETED)
        /// Update ChunkMaster only
        /// </summary>
        private async Task UpdateChunkSuccessAsync(ChunkResultUpdateCommand message)
        {
            var policy = _statusRetryPolicyFactory.CreateRetryPolicy();

            await policy.ExecuteAsync(async () =>
            {
                await ExecuteWithTransactionAsync(
                    () => _chunkMasterCommandRepository.UpdateChunkMasterResult(BuildUpdateCommand(message)),
                    $"[ChunkResultService] Chunk success transaction failed. ChunkId={message.ChunkId}");
            });
        }

        /// <summary>
        /// Handle chunk partial success (PARTIAL_SUCCESS)
        /// 1. Insert ChunkFail (for failed items tracking)
        /// 2. Update ChunkMaster (PARTIAL_SUCCESS status)
        /// </summary>
        private async Task UpdateChunkPartialSuccessAsync(ChunkResultUpdateCommand message)
        {
            var policy = _statusRetryPolicyFactory.CreateRetryPolicy();

            await policy.ExecuteAsync(async () =>
            {
                await ExecuteWithTransactionAsync(
                    () =>
                    {
                        // Insert ChunkFail for audit trail
                        _chunkFailCommandRepository.CreateChunkFail(BuildFailCommand(message, 0));

                        // Update ChunkMaster with PARTIAL_SUCCESS status
                        _chunkMasterCommandRepository.UpdateChunkMasterResult(BuildUpdateCommand(message));
                    },
                    $"[ChunkResultService] Chunk partial success transaction failed. ChunkId={message.ChunkId}");
            });

            _logger.LogInformation(
                AppLog.Log("[ChunkResultService] PARTIAL_SUCCESS processed. ChunkId={ChunkId}, ErrorCode={ErrorCode}"),
                message.ChunkId, message.ErrorCode);
        }

        /// <summary>
        /// Handle chunk failure (FAILED)
        /// 1. Insert ChunkFail
        /// 2. Update ChunkMaster (FAILED status)
        /// </summary>
        private async Task UpdateChunkFailAsync(ChunkResultUpdateCommand message)
        {
            var policy = _statusRetryPolicyFactory.CreateRetryPolicy();

            await policy.ExecuteAsync(async () =>
            {
                await ExecuteWithTransactionAsync(
                    () =>
                    {
                        // Insert ChunkFail
                        _chunkFailCommandRepository.CreateChunkFail(BuildFailCommand(message, 0));

                        // Update ChunkMaster
                        _chunkMasterCommandRepository.UpdateChunkMasterResult(BuildUpdateCommand(message));
                    },
                    $"[ChunkResultService] Chunk fail transaction failed. ChunkId={message.ChunkId}");
            });

            _logger.LogInformation(
                AppLog.Log("[ChunkResultService] FAILED processed. ChunkId={ChunkId}, ErrorCode={ErrorCode}"),
                message.ChunkId, message.ErrorCode);
        }

        #endregion

        #region === Private: Job Completion Logic ===

        private async Task<bool> CheckJobCompleteAndPublishAsync(Guid jobId, int totalChunkCount)
        {
            var policy = _redisRetryPolicyFactory.CreateRetryPolicy();

            var (exists, completed) = await policy.ExecuteAsync(async () =>
                await _jobCompleteChunkManager.IncrementAndCheckCompletedAsync(jobId, totalChunkCount));

            if (!exists)
            {
                _logger.LogWarning(
                    AppLog.Log("[ChunkResultService] Job not found in Redis. JobId={JobId}"), jobId);

                // TODO : 20251216 - 로직 수정 필요
                var statusPolicy = _statusRetryPolicyFactory.CreateRetryPolicy();
                completed = await statusPolicy.ExecuteAsync(async () =>
                    await _chunkMasterQueryRespository.CheckCompleteByJobId(jobId));
            }

            return completed;
        }

        private async Task<AllChunkResult_DTO> CheckAllChunkComplete(ChunkResultUpdateCommand message, AllChunkResult_DTO result)
        {
            result.IsComplete = await CheckJobCompleteAndPublishAsync(message.JobId, message.TotalChunkCount);

            if (result.IsComplete)
            {
                var policy = _statusRetryPolicyFactory.CreateRetryPolicy();

                var chunksResult = await policy.ExecuteAsync(async () =>
                    await _chunkMasterQueryRespository.GetChunksResultByJobId(message.JobId));

                result.AllChunkStatus = StatusHelper.GetJobStatus(message.TotalChunkCount, chunksResult);
                result.TotalChunkCount = chunksResult.TotalChunkCount;
                result.SuccessCount = chunksResult.SuccessCount;
                result.PartialSuccessCount = chunksResult.PartialSuccessCount;
                result.FailedCount = chunksResult.FailedCount;
            }

            return result;
        }

        #endregion

        #region === Private: Transaction Helper ===

        private async Task ExecuteWithTransactionAsync(Action action, string errorLog)
        {
            try
            {
                action();
                await _unitOfWork.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _unitOfWork.Rollback();
                _logger.LogError(ex, AppLog.Log(errorLog));
                throw;
            }
        }

        #endregion

        #region === Private: Command Builders ===

        /// <summary>
        /// Build update command for ChunkMaster
        /// Works for all statuses (COMPLETED/PARTIAL_SUCCESS/FAILED)
        /// </summary>
        private UpdateChunkMasterResult_Command BuildUpdateCommand(ChunkResultUpdateCommand msg)
            => new()
            {
                ChunkId = msg.ChunkId,
                ProcessedItemCount = msg.ProcessedItemCount,
                Status = msg.Status,
                HandlerName = msg.HandlerName,
                StartedAt = msg.StartedAt,
                CompletedAt = msg.CompletedAt,
                ErrorMessage = msg.Status == ChunkScopeStatus.COMPLETED.ToString()
                    ? msg.Message
                    : msg.ErrorMessage ?? msg.Message
            };

        /// <summary>
        /// Build ChunkFail command
        /// Used for both PARTIAL_SUCCESS and FAILED
        /// </summary>
        private CreateChunkFail_Command BuildFailCommand(ChunkResultUpdateCommand msg, int retryCount)
            => new()
            {
                ChunkFailureId = IdentifierGenerator.GetSequentialGUID(),
                ChunkId = msg.ChunkId,
                JobId = msg.JobId,
                Identifier = msg.Identifier,
                ErrorCode = msg.ErrorCode,
                ErrorMessage = msg.ErrorMessage,
                ErrorDetail = msg.ErrorDetail,
                RetryCount = retryCount,
                IsRetryable = msg.IsRetryable
            };

        #endregion
    }
}
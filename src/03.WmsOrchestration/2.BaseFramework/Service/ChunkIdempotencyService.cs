using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;

namespace Portfolio.WmsOrchestration.Service
{
    public class ChunkIdempotencyService
    {
        private readonly ILogger<ChunkIdempotencyService> _logger;
        private readonly ChunkMasterQueryRepository _chunkMasterQueryRespository;
        private readonly OrchestrationRethriveRetryPolicyFactory _retryPolicyFactory;

        public ChunkIdempotencyService(
            ILogger<ChunkIdempotencyService> logger,
            ChunkMasterQueryRepository chunkMasterQueryRespository,
            OrchestrationRethriveRetryPolicyFactory retryPolicyFactory)
        {
            _logger = logger;
            _chunkMasterQueryRespository = chunkMasterQueryRespository;
            _retryPolicyFactory = retryPolicyFactory;
        }

        public async Task<IdempotencyResult> CheckChunkResultIdempotencyAsync(Guid chunkId)
        {
            var policy = _retryPolicyFactory.CreateRetryPolicy();

            string? status = await policy.ExecuteAsync(async () =>
                await GetChunkStatusAsync(chunkId));

            if (status != null && status == ChunkScopeStatus.PENDING.ToString())
            {
                return IdempotencyResult.NONE;
            }
            else
            {
                return IdempotencyResult.DUPLICATE;
            }
        }

        private async Task<string?> GetChunkStatusAsync(Guid chunkId)
        {
            return await _chunkMasterQueryRespository.GetColumnVlaue<string>(chunkId, nameof(ChunkMaster_Entity.Status));
        }
    }
}
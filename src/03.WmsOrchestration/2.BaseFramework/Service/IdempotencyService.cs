using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;

namespace Portfolio.WmsOrchestration.Service
{
    public class IdempotencyService
    {
        private readonly ILogger<IdempotencyService> _logger;
        private readonly IdempotencyRepository _idempotencyRepository;

        public IdempotencyService(
            ILogger<IdempotencyService> logger, 
            IdempotencyRepository idempotencyRepository
            )
        {
            _logger = logger;
            _idempotencyRepository = idempotencyRepository;
        }

        public async Task<(IdempotencyResult, ChunkScopeStatus)> CheckIdempotency(string wmsDocNo, int currentPage)
        {
            string? resultCode = await _idempotencyRepository.CheckIdempotency(wmsDocNo, currentPage);

            if (resultCode == null) return (IdempotencyResult.NONE, ChunkScopeStatus.PENDING);
            else if (resultCode != null && resultCode == ReqHeaderResultCode.SUCCESS) return (IdempotencyResult.SUCCESS, ChunkScopeStatus.COMPLETED);
            else if (resultCode != null && resultCode == ReqHeaderResultCode.FAIL) return (IdempotencyResult.FAIL, ChunkScopeStatus.FAILED);
            else if (resultCode != null && resultCode == ReqHeaderResultCode.PARTIAL_SUCCESS) return (IdempotencyResult.FAIL, ChunkScopeStatus.PARTIAL_SUCCESS);
            else return (IdempotencyResult.NONE, ChunkScopeStatus.PENDING);
        }
    }
}

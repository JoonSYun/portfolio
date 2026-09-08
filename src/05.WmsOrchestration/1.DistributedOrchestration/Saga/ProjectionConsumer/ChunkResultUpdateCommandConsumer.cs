using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Unified Chunk Result Update Consumer
    /// Handles COMPLETED / PARTIAL_SUCCESS / FAILED
    /// </summary>
    public class ChunkResultUpdateCommandConsumer
        : ChunkConsumerBase<ChunkResultUpdateCommand>
    {
        private readonly ChunkResultService _chunkResultService;

        public ChunkResultUpdateCommandConsumer(
            ILogger<ChunkResultUpdateCommandConsumer> logger,
            ChunkIdempotencyService chunkIdempotencyService,
            JobSagaService jobSagaService,
            ChunkResultService chunkResultService)
            : base(logger, chunkIdempotencyService, jobSagaService)
        {
            _chunkResultService = chunkResultService;
        }

        protected override async Task ExecuteAsync(ConsumeContext<ChunkResultUpdateCommand> context)
        {
            var result = await _chunkResultService.ExecuteChunkResult(context.Message);

            if (result.IsComplete)
            {
                await PublishAllChunkComplete(context, result);
            }
        }

        protected override async Task<IdempotencyResult> CheckIdempotencyAsync(ConsumeContext<ChunkResultUpdateCommand> context)
        {
            return await ChunkIdempotencyService.CheckChunkResultIdempotencyAsync(context.Message.ChunkId);
        }

        protected override void LogPreExecution(ChunkResultUpdateCommand command)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Received chunk result: JobId={JobId}, ChunkId={ChunkId}, Status={Status}, ProcessedCount={Count}"),
                GetType().Name, command.JobId, command.ChunkId, command.Status, command.ProcessedItemCount);
        }

        protected override void LogPostExecution(ChunkResultUpdateCommand command)
        {
            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] Complete chunk result: ChunkId={ChunkId}, Status={Status}"),
                GetType().Name, command.ChunkId, command.Status);
        }

        protected override void LogError(Exception ex, ChunkResultUpdateCommand command)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] ERROR chunk result: ChunkId={ChunkId}, Status={Status}"),
                GetType().Name, command.ChunkId, command.Status);
        }
    }
}
using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Repository;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Job Publish Consumer
    /// 
    /// Responsibilities:
    ///   - Query ChunkMaster by JobId (excluding Payload)
    ///   - Publish ChunkStepInitEvent for each chunk in parallel
    ///   - Payload is loaded later in LoadChunkStepPlanActivity for memory efficiency
    /// 
    /// Differences from legacy JobPublishCommandConsumer:
    ///   - Payload query removed
    ///   - Hangfire dependency removed
    ///   - Parallel publishing for better performance
    /// </summary>
    public class JobPublishConsumer : IConsumer<JobPublishCommand>
    {
        private readonly ILogger<JobPublishConsumer> _logger;
        private readonly ChunkMasterQueryRepository _chunkQueryRepository;

        // Limit concurrent publishing to avoid resource exhaustion
        private const int MAX_PARALLEL_PUBLISH = 50;

        public JobPublishConsumer(
            ILogger<JobPublishConsumer> logger,
            ChunkMasterQueryRepository chunkQueryRepository)
        {
            _logger = logger;
            _chunkQueryRepository = chunkQueryRepository;
        }

        public async Task Consume(ConsumeContext<JobPublishCommand> context)
        {
            var command = context.Message;

            try
            {
                _logger.LogInformation(
                    AppLog.Log("[JobPublishConsumer] Start publishing chunks. JobId={JobId}, Domain={DomainType}"),
                    command.JobId, command.DomainType);

                // 1. Query chunk list only (exclude Payload)
                List<GetChunkInfo_Qeury> chunkInfos = await _chunkQueryRepository.GetChunkInfo(command.JobId);

                if (!chunkInfos.Any())
                {
                    _logger.LogWarning(
                        AppLog.Log("[JobPublishConsumer] No chunks found. JobId={JobId}"),
                        command.JobId);
                    throw new InvalidOperationException($"No chunks found for JobId: {command.JobId}");
                }

                // 2. Publish events in parallel with concurrency limit
                await Parallel.ForEachAsync(
                    chunkInfos,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MAX_PARALLEL_PUBLISH
                    },
                    async (chunkInfo, cancellationToken) =>
                    {
                        var evt = new ChunkStepInitEvent
                        {
                            JobId = command.JobId,
                            DomainType = command.DomainType,
                            TotalChunkCount = command.TotalChunkCount,
                            Delimiter = command.Delimiter,
                            UserId = command.UserId,
                            Priority = command.Priority,
                            ChunkId = chunkInfo.ChunkId,
                            Identifier = chunkInfo.Identifier,
                            ChunkIndex = chunkInfo.ChunkIndex,
                            RequestBrandCode = command.RequestBrandCode,
                            SlipDiv = command.SlipDiv
                        };

                        await context.Publish(evt, x => x.CorrelationId = chunkInfo.ChunkId, cancellationToken);

                        
                    });

                _logger.LogInformation(
                    AppLog.Log("[JobPublishConsumer] Completed publishing {Count} chunks. JobId={JobId}"),
                    chunkInfos.Count, command.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[JobPublishConsumer] Failed to publish chunks. JobId={JobId}"),
                    command.JobId);
                throw;
            }
        }
    }
}
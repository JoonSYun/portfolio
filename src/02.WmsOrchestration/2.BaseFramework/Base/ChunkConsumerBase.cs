// [담당업무 2] Base Framework — Chunk 수준 Consumer 공통 상위 클래스 (멱등성 · AllChunkComplete 발행).

using MassTransit;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Saga;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Base Chunk Consumer abstract class.
    /// Unifies logging and exception handling, actual business logic implemented in derived classes.
    /// </summary>
    /// <typeparam name="TCommand">Command type to process</typeparam>
    public abstract class ChunkConsumerBase<TCommand> : IConsumer<TCommand>
        where TCommand : class, IChunkMessage
    {
        protected readonly ILogger<ChunkConsumerBase<TCommand>> Logger;
        protected readonly ChunkIdempotencyService ChunkIdempotencyService;
        protected readonly JobSagaService JobSagaService;
        protected readonly string ConsumerType;

        protected ChunkConsumerBase(
            ILogger<ChunkConsumerBase<TCommand>> logger,
            ChunkIdempotencyService chunkIdempotencyService,
            JobSagaService jobSagaService
            )
        {
            Logger = logger;
            ChunkIdempotencyService = chunkIdempotencyService;
            JobSagaService = jobSagaService;
            ConsumerType = GetType().Name;
        }

        public async Task Consume(ConsumeContext<TCommand> context)
        {
            try
            {
                // Pre-execution log
                LogPreExecution(context.Message);

                if (!await HandleIdempotencyAsync(context))
                {
                    // Execute actual business logic
                    await ExecuteAsync(context);

                    // Post-execution log
                    LogPostExecution(context.Message);
                }
            }
            catch (Exception ex)
            {
                LogError(ex, context.Message);

                await ExecuteCompensateAsync(context.Message);

                throw;
            }
        }

        /// <summary>
        /// Publish AllChunkComplete event to JobSaga
        /// Called when all chunks are completed
        /// </summary>
        protected async Task PublishAllChunkComplete<T>(ConsumeContext<T> context, AllChunkResult_DTO result)
            where T : class, IChunkMessage
        {
            Logger.LogInformation(
                AppLog.Log("[{Consumer}] All chunks completed. Publishing AllChunkComplete event: JobId={JobId}, Status={Status}, TotalChunks={Total}, Success={Success}, PartialSuccess={Partial}, Failed={Failed}"),
                ConsumerType,
                context.Message.JobId,
                result.AllChunkStatus,
                result.TotalChunkCount,
                result.SuccessCount,
                result.PartialSuccessCount,
                result.FailedCount);

            var jobCompleteEvent = new AllChunkCompleteEvent
            {
                JobId = context.Message.JobId,
                TotalChunkCount = context.Message.TotalChunkCount,
                Delimiter = context.Message.Delimiter,
                DomainType = context.Message.DomainType,
                UserId = context.Message.UserId,
                Status = result.AllChunkStatus,
                ChunkResult = result
            };

            await context.Publish(jobCompleteEvent, ctx => ctx.CorrelationId = context.Message.JobId);
        }

        /// <summary>
        /// Checks chunk idempotency to prevent duplicate processing.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>
        /// true : Chunk already processed successfully. Skip processing.
        /// false : Proceed with processing.
        /// </returns>
        private async Task<bool> HandleIdempotencyAsync(ConsumeContext<TCommand> context)
        {
            bool valid = false;
            var idempotencyResult = await CheckIdempotencyAsync(context);

            if (idempotencyResult == IdempotencyResult.DUPLICATE)
            {
                valid = true;

                Logger.LogWarning(
                    AppLog.Log("[{Consumer}] Chunk result already processed. Skipping: JobId={JobId}, ChunkId={ChunkId}, ChunkIndex={ChunkIndex}"),
                    ConsumerType,
                    context.Message.JobId,
                    context.Message.ChunkId,
                    context.Message.ChunkIndex);
            }
            else if (idempotencyResult == IdempotencyResult.NONE)
            {
                valid = false;

                Logger.LogDebug(
                    AppLog.Log("[{Consumer}] No prior processing found. Proceeding: JobId={JobId}, ChunkId={ChunkId}, ChunkIndex={ChunkIndex}"),
                    ConsumerType,
                    context.Message.JobId,
                    context.Message.ChunkId,
                    context.Message.ChunkIndex);
            }

            return valid;
        }

        /// <summary>
        /// Execute actual business logic.
        /// Must be implemented by derived classes.
        /// </summary>
        protected abstract Task ExecuteAsync(ConsumeContext<TCommand> context);

        /// <summary>
        /// Check idempotency to prevent duplicate processing.
        /// Override in derived classes for actual implementation.
        /// </summary>
        protected virtual async Task<IdempotencyResult> CheckIdempotencyAsync(ConsumeContext<TCommand> context)
        {
            Logger.LogWarning(
                AppLog.Log("[{ConsumerType}] Default CheckIdempotencyAsync called. Assuming no prior processing: JobId={JobId}, ChunkId={ChunkId}, ChunkIndex={ChunkIndex}"),
                ConsumerType,
                context.Message.JobId,
                context.Message.ChunkId,
                context.Message.ChunkIndex);

            return IdempotencyResult.NONE;
        }

        /// <summary>
        /// Pre-execution log (command received).
        /// Override in derived classes if needed.
        /// </summary>
        protected virtual void LogPreExecution(TCommand command)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Received command"),
                ConsumerType);
        }

        /// <summary>
        /// Post-execution log (completed).
        /// Override in derived classes if needed.
        /// </summary>
        protected virtual void LogPostExecution(TCommand command)
        {
            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] Completed command"),
                ConsumerType);
        }

        /// <summary>
        /// Error log.
        /// Override in derived classes if needed.
        /// </summary>
        protected virtual void LogError(Exception ex, TCommand command)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] ERROR processing command"),
                ConsumerType);
        }

        /// <summary>
        /// Execute compensation logic.
        /// Override in derived classes if compensation is needed.
        /// </summary>
        protected virtual async Task ExecuteCompensateAsync(TCommand command)
        {
            Logger.LogWarning(
                AppLog.Log("[{ConsumerType}] Compensate not implemented for command"),
                ConsumerType);
        }
    }
}
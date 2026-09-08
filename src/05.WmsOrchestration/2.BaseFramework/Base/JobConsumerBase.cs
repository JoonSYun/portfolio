// [담당업무 2] Base Framework — Job 수준 Consumer 공통 상위 클래스 (로깅 · 멱등성 · 보상 훅).

using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Saga;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// 기본 Projection Consumer 추상 클래스.
    /// 로깅과 예외 처리를 통일하고, 실제 비즈니스 로직은 파생 클래스에서 구현.
    /// </summary>
    /// <typeparam name="TCommand">처리할 Command 타입</typeparam>
    public abstract class JobConsumerBase<TCommand> : IConsumer<TCommand> 
        where TCommand : class, IJobSagaEvent
    {
        protected readonly ILogger<JobConsumerBase<TCommand>> Logger;
        protected readonly JobIdempotencyService JobIdempotencyService;
        protected readonly string ConsumerType;

        protected JobConsumerBase(
            ILogger<JobConsumerBase<TCommand>> logger,
            JobIdempotencyService jobIdempotencyService
            )
        {
            Logger = logger;
            JobIdempotencyService = jobIdempotencyService;
            ConsumerType = GetType().Name;
        }

        public async Task Consume(ConsumeContext<TCommand> context)
        {
            try
            {
                // 사전 로그
                LogPreExecution(context.Message);

                if (!await HandleIdempotencyAsync(context))
                {
                    // 실제 비즈니스 로직 실행
                    await ExecuteAsync(context.Message);

                    // 사후 로그
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
                    AppLog.Log("[{Consumer}] Chunk result already processed successfully. Skipping processing: JobId={JobId}"),
                    ConsumerType,
                    context.Message.JobId);
            }
            else if (idempotencyResult == IdempotencyResult.NONE)
            {
                valid = false;

                Logger.LogDebug(
                    AppLog.Log("[{Consumer}] No prior processing found. Proceeding with processing: JobId={JobId}"),
                    ConsumerType,
                    context.Message.JobId);
            }

            return valid;
        }

        /// <summary>
        /// 실제 비즈니스 로직을 수행하는 추상 메서드.
        /// 파생 클래스에서 구현해야 함.
        /// </summary>
        protected abstract Task ExecuteAsync(TCommand command);

        protected virtual async Task<IdempotencyResult> CheckIdempotencyAsync(ConsumeContext<TCommand> context)
        {
            Logger.LogWarning(
                AppLog.Log("[{ConsuerType}] Default CheckIdempotencyAsync called. Assuming no prior processing: JobId={JobId}"),
                ConsumerType,
                context.Message.JobId);

            return IdempotencyResult.NONE;
        }

        /// <summary>
        /// 사전 로그 (명령 수신).
        /// 파생 클래스에서 필요시 오버라이드.
        /// </summary>
        protected virtual void LogPreExecution(TCommand command)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Received command"),
                ConsumerType);
        }

        /// <summary>
        /// 사후 로그 (완료).
        /// 파생 클래스에서 필요시 오버라이드.
        /// </summary>
        protected virtual void LogPostExecution(TCommand command)
        {
            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] Completed command"),
                ConsumerType);
        }

        /// <summary>
        /// 에러 로그.
        /// 파생 클래스에서 필요시 오버라이드.
        /// </summary>
        protected virtual void LogError(Exception ex, TCommand command)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] ERROR processing command"),
                ConsumerType);
        }

        protected virtual async Task ExecuteCompensateAsync(TCommand command)
        {
            Logger.LogWarning(
                AppLog.Log("[{ConsumerType}] Compensate not implemented for command"),
                ConsumerType);
        }
    }
}

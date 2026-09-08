// [담당업무 2] Base Framework — Job 후처리(Process) Consumer 공통 상위 클래스 (성공/실패 이벤트 발행 표준화).

using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Job Process Consumer Base
    /// 
    /// 책임:
    ///   - Job Process 실행 공통 파이프라인
    ///   - ExecuteAsync 호출
    ///   - 성공/실패 이벤트 발행
    ///   - 로그 처리
    /// 
    /// 패턴:
    ///   - JobConsumerBase와 유사하지만 멱등성 체크 없음
    ///   - Process는 JobProcessSaga가 순차 실행 보장
    /// </summary>
    public abstract class JobProcessConsumerBase<TEvent> : IConsumer<TEvent>
        where TEvent : JobProcessDomainEventBase
    {
        protected readonly ILogger Logger;
        protected readonly string ConsumerType;

        protected JobProcessConsumerBase(ILogger logger)
        {
            Logger = logger;
            ConsumerType = GetType().Name;
        }

        public async Task Consume(ConsumeContext<TEvent> context)
        {
            var msg = context.Message;
            var startedAt = DateTime.UtcNow;

            try
            {
                // 1. 사전 로그
                LogPreExecution(msg);

                // 2. 실행
                await ExecuteAsync(msg);

                // 3. 성공 이벤트 발행
                var completedAt = DateTime.UtcNow;
                await PublishCompletedAsync(context, msg, startedAt, completedAt);

                // 4. 사후 로그
                LogPostExecution(msg);
            }
            catch (Exception ex)
            {
                // 5. 에러 로그
                LogError(ex, msg);

                // 6. 실패 이벤트 발행
                var completedAt = DateTime.UtcNow;
                await PublishFailedAsync(context, msg, ex, startedAt, completedAt);

                // 7. 예외 전파 안함 (Saga가 FailedEvent로 처리)
            }
        }

        // ==========================================
        // Abstract Methods - 하위 클래스 구현
        // ==========================================

        /// <summary>
        /// Process 실행 로직
        /// </summary>
        protected abstract Task ExecuteAsync(TEvent message);
        
        /// <summary>
        /// 실행 전 로그
        /// </summary>
        protected virtual void LogPreExecution(TEvent message)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Starting process: JobId={JobId}, ProcessId={ProcessId}, ProcessIndex={ProcessIndex}, ProcessType={ProcessType}"),
                ConsumerType, message.JobId, message.ProcessId, message.ProcessIndex, message.ProcessType);
        }

        /// <summary>
        /// 실행 후 로그
        /// </summary>
        protected virtual void LogPostExecution(TEvent message)
        {
            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] Completed process: JobId={JobId}, ProcessId={ProcessId}, ProcessIndex={ProcessIndex}, ProcessType={ProcessType}"),
                ConsumerType, message.JobId, message.ProcessId, message.ProcessIndex, message.ProcessType);
        }

        /// <summary>
        /// 에러 로그
        /// </summary>
        protected virtual void LogError(Exception ex, TEvent message)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] Error in process: JobId={JobId}, ProcessId={ProcessId}, ProcessIndex={ProcessIndex}, ProcessType={ProcessType}, Error={ErrorMessage}"),
                ConsumerType, message.JobId, message.ProcessId, message.ProcessIndex, message.ProcessType, ex.Message);
        }

        // ==========================================
        // Event Publishing
        // ==========================================

        protected IEnumerable<T>? GetPayload<T>(TEvent message) where T : class
        {
            if (string.IsNullOrWhiteSpace(message.PayloadJson))
            {
                Logger.LogWarning(
                    AppLog.Log("[{ConsumerType}] PayloadJson is null or empty. JobId={JobId}"),
                    ConsumerType, message.JobId);
                return null;
            }

            try
            {
                // 1단계: List<string>으로 역직렬화
                var payloadStringList = JsonConvert.DeserializeObject<List<string>>(message.PayloadJson);

                if (payloadStringList == null || payloadStringList.Count == 0)
                {
                    Logger.LogWarning(
                        AppLog.Log("[{ConsumerType}] PayloadStringList is null or empty. JobId={JobId}"),
                        ConsumerType, message.JobId);
                    return null;
                }

                // 2단계: 각 string을 List<T>로 변환 후 평탄화
                var result = payloadStringList
                    .SelectMany(jsonString => JsonConvert.DeserializeObject<List<T>>(jsonString) ?? new List<T>())
                    .ToList();

                if (result.Count == 0)
                {
                    Logger.LogWarning(
                        AppLog.Log("[{ConsumerType}] No items found after deserialization. JobId={JobId}"),
                        ConsumerType, message.JobId);
                    return null;
                }

                Logger.LogDebug(
                    AppLog.Log("[{ConsumerType}] Successfully deserialized payload. JobId={JobId}, ItemCount={Count}"),
                    ConsumerType, message.JobId, result.Count);

                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log("[{ConsumerType}] Failed to deserialize payload. JobId={JobId}, Error={Error}"),
                    ConsumerType, message.JobId, ex.Message);
                return null;
            }
        }

        private Task PublishCompletedAsync(
            ConsumeContext<TEvent> context,
            TEvent msg,
            DateTime startedAt,
            DateTime completedAt)
        {
            var evt = new ProcessCompleteEvent
            {
                JobId = msg.JobId,
                ProcessId = msg.ProcessId,
                ProcessIndex = msg.ProcessIndex,
                ProcessType = msg.ProcessType,
                Status = "COMPLETED",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Message = $"{ConsumerType} completed successfully"
            };

            Logger.LogInformation(
                AppLog.Log("[{Consumer}] Published ProcessCompleted: JobId={JobId}, ProcessIndex={ProcessIndex}"),
                ConsumerType, msg.JobId, msg.ProcessIndex);

            return context.Publish(evt, x => x.CorrelationId = msg.JobId);
        }

        private Task PublishFailedAsync(
            ConsumeContext<TEvent> context,
            TEvent msg,
            Exception ex,
            DateTime startedAt,
            DateTime completedAt)
        {
            var evt = new ProcessFailEvent
            {
                JobId = msg.JobId,
                ProcessId = msg.ProcessId,
                ProcessIndex = msg.ProcessIndex,
                ProcessType = msg.ProcessType,
                Status = "FAILED",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ErrorCode = ex.GetType().Name,
                ErrorMessage = ex.Message,
                ErrorDetail = ex.ToString(),
                RetryCount = 0,
                IsRetryable = false
            };

            Logger.LogWarning(
                AppLog.Log("[{Consumer}] Published ProcessFailed: JobId={JobId}, ProcessIndex={ProcessIndex}, Error={Error}"),
                ConsumerType, msg.JobId, msg.ProcessIndex, ex.Message);

            return context.Publish(evt, x => x.CorrelationId = msg.JobId);
        }
    }
}
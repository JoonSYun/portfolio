// [담당업무 2] Base Framework — Step Consumer 공통 상위 클래스. 정상/보상 모드 분기, 이중 멱등성, 결과 이벤트 발행을 여기서 끝내 도메인 Consumer 는 비즈니스 로직만 쓴다.

using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Portfolio.WmsOrchestration.Exceptions;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using System.Diagnostics;
using System.Text;
using static Portfolio.WmsOrchestration.Model.Register;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Unified Step Consumer Base for Chunk-Step processing.
    ///
    /// Responsibilities:
    ///   1) Normal mode   (IsCompensation = false) -> ProcessAsync()
    ///   2) Compensation  (IsCompensation = true)  -> CompensateAsync()
    ///
    /// Design intent:
    ///   - One consumer handles a single event type and branches into normal/compensation flows.
    ///   - Publishing result events is standardized here to keep domain consumers thin.
    ///   - Idempotency checks are enforced in both normal and compensation flows.
    /// </summary>
    public abstract class ChunkStepConsumerBase<TEvent, TPayload> : IConsumer<TEvent>
        where TEvent : ChunkStepDomainEventBase
    {
        // =========================================================================
        // Dependencies / Identity
        // =========================================================================

        /// <summary>Structured logger (DI)</summary>
        protected readonly ILogger Logger;

        /// <summary>Concrete consumer type name (for logs)</summary>
        protected readonly string ConsumerType;

        // =========================================================================
        // Runtime Context (set per message)
        // =========================================================================

        /// <summary>
        /// Deserialized request payload list for the current message.
        /// - Always non-null after successful ConvertPayloadsOrThrow().
        /// - Kept as protected to allow derived classes to read it.
        /// </summary>
        protected IList<TPayload> RequestPayload { get; private set; } = new List<TPayload>();

        /// <summary>
        /// Flattened list of identifiers to compensate or to filter (when partial failure exists).
        /// - Null means "no partial failure details provided".
        /// </summary>
        protected IList<string>? FailedIdentifiers { get; private set; }

        /// <summary>
        /// JSON serializer settings:
        /// - Keeps casing stable
        /// - Avoids unexpected property-name issues
        /// - Does not over-constrain (practical default)
        /// </summary>
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new DefaultNamingStrategy()
            },
            NullValueHandling = NullValueHandling.Include
        };

        protected ChunkStepConsumerBase(ILogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ConsumerType = GetType().Name;
        }

        // =========================================================================
        // Entry
        // =========================================================================

        public async Task Consume(ConsumeContext<TEvent> context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));

            var evt = context.Message;
            if (evt is null)
                throw new InvalidOperationException("ConsumeContext.Message is null.");

            // Parse message payloads once (fail-fast if malformed).
            ConvertPayloadsOrThrow(evt);

            // Branch by mode.
            if (evt.IsCompensation)
                await HandleCompensationAsync(context).ConfigureAwait(false);
            else
                await HandleProcessAsync(context).ConfigureAwait(false);
        }

        // =========================================================================
        // Payload conversion
        // =========================================================================

        /// <summary>
        /// Converts JSON fields in the event to strongly-typed runtime fields.
        /// Fail-fast behavior:
        ///   - If PayloadJson is invalid, throws (so the message becomes faulted/retried per MT policy).
        /// Practical reason:
        ///   - Silently swallowing deserialize errors often leads to null reference issues later,
        ///     producing confusing logs and inconsistent event publishing.
        /// </summary>
        private void ConvertPayloadsOrThrow(TEvent evt)
        {
            try
            {
                // PayloadJson is mandatory for step execution.
                // Missing payload means the message itself is invalid.
                if (string.IsNullOrWhiteSpace(evt.PayloadJson))
                    throw new InvalidOperationException("PayloadJson is empty.");

                var payloads = JsonConvert.DeserializeObject<IList<TPayload>>(evt.PayloadJson, JsonSettings);

                // Deserialization succeeded but resulted in null -> malformed message
                if (payloads is null)
                    throw new InvalidOperationException("PayloadJson deserialized to null.");

                RequestPayload = payloads;

                // Optional: partial failure details (StepIndex -> Identifiers)
                if (!string.IsNullOrWhiteSpace(evt.PartialFailureDetailsJson))
                {
                    var map = JsonConvert.DeserializeObject<Dictionary<int, List<string>>>(
                        evt.PartialFailureDetailsJson,
                        JsonSettings);

                    if (map is null)
                        throw new InvalidOperationException("PartialFailureDetailsJson deserialized to null.");

                    FailedIdentifiers = map.Values
                        .SelectMany(x => x ?? Enumerable.Empty<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .ToList();
                }
                else
                {
                    FailedIdentifiers = null;
                }
            }
            catch (JsonException jex)
            {
                // JSON format / schema mismatch
                Logger.LogError(
                    jex,
                    AppLog.Log(
                        "[{ConsumerType}] Payload JSON deserialization failed. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}"),
                    ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.StepType);

                // Let MassTransit handle retry / fault routing
                throw;
            }
            catch (Exception ex)
            {
                // Any other unexpected error during payload preparation
                Logger.LogError(
                    ex,
                    AppLog.Log(
                        "[{ConsumerType}] Unexpected error while converting payloads. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}"),
                    ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.StepType);

                throw;
            }
        }

        // =========================================================================
        // 1) Normal execution
        // =========================================================================

        private async Task HandleProcessAsync(ConsumeContext<TEvent> context)
        {
            var evt = context.Message;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Idempotency (normal)
                if (await SkipIfDuplicatedAsync(context).ConfigureAwait(false))
                    return;

                LogPreExecution(evt);

                // Execute business logic in derived class
                var response = await ProcessAsync(context).ConfigureAwait(false);

                stopwatch.Stop();

                // Standardized result routing
                switch (response.ResultType)
                {
                    case StepResultType.SUCCESS:
                        await PublishResultEventAsync(context, response, stopwatch.Elapsed).ConfigureAwait(false);
                        LogPostExecution(evt, response);
                        break;

                    case StepResultType.PARTIAL_SUCCESS:
                        await PublishResultEventAsync(context, response, stopwatch.Elapsed).ConfigureAwait(false);
                        LogPartialSuccess(evt, response);
                        break;

                    case StepResultType.BUSINESS_FAIL:
                        await PublishResultEventAsync(context, response, stopwatch.Elapsed).ConfigureAwait(false);
                        LogBusinessFail(evt, response);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown ResultType: {response.ResultType}");
                }
            }
            catch (BusinessException bex)
            {
                // BusinessException: publish FAIL event with a consistent format.
                stopwatch.Stop();

                var response = StepConsumerResponse.BusinessFail(
                    errorCode: bex.GetType().Name,
                    errorMessage: bex.Message,
                    errorDetail: bex.InnerException?.ToString() ?? bex.StackTrace
                );

                await PublishResultEventAsync(context, response, stopwatch.Elapsed).ConfigureAwait(false);
                LogError(bex, evt);
            }
            catch (Exception ex)
            {
                // Unexpected exception: publish FAIL event and rethrow is optional.
                // Practical approach here:
                //   - publish fail event for observability
                //   - DO NOT swallow errors silently; if you want retry, rethrow.
                stopwatch.Stop();

                var response = StepConsumerResponse.BusinessFail(
                    errorCode: ex.GetType().Name,
                    errorMessage: ex.Message,
                    errorDetail: ex.StackTrace
                );

                await PublishResultEventAsync(context, response, stopwatch.Elapsed).ConfigureAwait(false);
                LogError(ex, evt);

                // If your infra uses retry policies (recommended), rethrow:
                // throw;
            }
        }

        // =========================================================================
        // 2) Compensation execution
        // =========================================================================

        private async Task HandleCompensationAsync(ConsumeContext<TEvent> context)
        {
            var evt = context.Message;

            try
            {
                // Idempotency (compensation)
                if (await SkipIfCompensationDuplicatedAsync(context).ConfigureAwait(false))
                    return;

                LogPreCompensation(evt);

                // Execute compensation in derived class
                await CompensateAsync(context).ConfigureAwait(false);

                LogPostCompensation(evt);

                // Notify CompensationSaga
                await PublishCompensationStepCompletedEventAsync(context).ConfigureAwait(false);
            }
            catch (BusinessException bex)
            {
                LogCompensationError(bex, evt);

                await PublishCompensationStepFailedEventAsync(context, bex).ConfigureAwait(false);

                // Same note as normal flow:
                // If you have retry policies for compensation, consider rethrowing.
                // throw;
            }
            catch (Exception ex)
            {
                LogCompensationError(ex, evt);

                await PublishCompensationStepFailedEventAsync(context, ex).ConfigureAwait(false);

                // throw;
            }
        }

        // =========================================================================
        // Idempotency
        // =========================================================================

        /// <summary>
        /// Normal mode idempotency gate.
        /// Behavior:
        ///   - If result exists (SUCCESS/PARTIAL_SUCCESS/FAILED), publish result event (skipped) and return true.
        ///   - Otherwise, return false and proceed.
        /// </summary>
        private async Task<bool> SkipIfDuplicatedAsync(ConsumeContext<TEvent> context)
        {
            var evt = context.Message;
            var (idempotencyResult, chunkStatus) = await CheckIdempotencyAsync(context).ConfigureAwait(false);

            if (idempotencyResult == IdempotencyResult.SUCCESS ||
                idempotencyResult == IdempotencyResult.FAIL)
            {
                Logger.LogWarning(
                    AppLog.Log("[{ConsumerType}] Duplicate result detected. Skipping. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, IdempotencyResult={Result}"),
                    ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, idempotencyResult);

                // Determine status from chunkStatus
                var status = chunkStatus == ChunkScopeStatus.COMPLETED
                    ? ChunkScopeStatus.COMPLETED.ToString()
                    : chunkStatus == ChunkScopeStatus.PARTIAL_SUCCESS
                        ? ChunkScopeStatus.PARTIAL_SUCCESS.ToString()
                        : ChunkScopeStatus.FAILED.ToString();

                var response = idempotencyResult == IdempotencyResult.SUCCESS
                    ? StepConsumerResponse.Success(0, "Duplicate - skipped")
                    : StepConsumerResponse.BusinessFail(
                        errorCode: "DUPLICATE_FAIL",
                        errorMessage: "Reprocessing previously failed chunk",
                        errorDetail: $"ChunkStatus: {chunkStatus}");

                await PublishResultEventAsync(
                    context,
                    response,
                    TimeSpan.Zero).ConfigureAwait(false);

                return true;
            }

            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] No prior result. Continue processing. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex);

            return false;
        }

        /// <summary>
        /// Compensation mode idempotency gate.
        /// Behavior:
        ///   - If already compensated successfully, publish "CompensationStepCompleted" again (safe) and skip.
        ///   - Otherwise, proceed to actual compensation.
        /// </summary>
        private async Task<bool> SkipIfCompensationDuplicatedAsync(ConsumeContext<TEvent> context)
        {
            var evt = context.Message;
            var idempotencyResult = await CheckCompensationIdempotencyAsync(context).ConfigureAwait(false);

            if (idempotencyResult == IdempotencyResult.SUCCESS)
            {
                Logger.LogWarning(
                    AppLog.Log("[{ConsumerType}] Compensation already processed. Skipping. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, CompensationId={CompensationId}"),
                    ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.CompensationId);

                // Re-publish completion (idempotent notification)
                await PublishCompensationStepCompletedEventAsync(context).ConfigureAwait(false);
                return true;
            }

            Logger.LogDebug(
                AppLog.Log("[{ConsumerType}] No prior compensation result. Continue compensating. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, CompensationId={CompensationId}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.CompensationId);

            return false;
        }

        // =========================================================================
        // Abstract methods (implemented by derived consumers)
        // =========================================================================

        /// <summary>Normal business logic for the step.</summary>
        protected abstract Task<StepConsumerResponse> ProcessAsync(ConsumeContext<TEvent> context);

        /// <summary>Compensation logic for the step.</summary>
        protected abstract Task CompensateAsync(ConsumeContext<TEvent> context);

        /// <summary>Normal idempotency check (SUCCESS/FAIL/UNKNOWN + chunk status).</summary>
        protected abstract Task<(IdempotencyResult, ChunkScopeStatus)> CheckIdempotencyAsync(ConsumeContext<TEvent> context);

        /// <summary>Compensation idempotency check (SUCCESS/UNKNOWN).</summary>
        protected abstract Task<IdempotencyResult> CheckCompensationIdempotencyAsync(ConsumeContext<TEvent> context);

        // =========================================================================
        // Event publishing (standardized)
        // =========================================================================

        /// <summary>
        /// Publish step result event (unified for SUCCESS/PARTIAL_SUCCESS/FAILED)
        /// </summary>
        private Task PublishResultEventAsync(
            ConsumeContext<TEvent> context,
            StepConsumerResponse response,
            TimeSpan elapsed)
        {
            var evt = context.Message;
            var now = DateTime.UtcNow;

            // Determine status based on ResultType
            string status;
            if (response.ResultType == StepResultType.SUCCESS)
            {
                status = ChunkScopeStatus.COMPLETED.ToString();
            }
            else if (response.ResultType == StepResultType.PARTIAL_SUCCESS)
            {
                status = ChunkScopeStatus.PARTIAL_SUCCESS.ToString();
            }
            else // BUSINESS_FAIL
            {
                status = ChunkScopeStatus.FAILED.ToString();
            }

            var resultEvent = new ChunkStepResultEvent
            {
                JobId = evt.JobId,
                ChunkId = evt.ChunkId,
                Identifier = evt.Identifier,
                ChunkIndex = evt.ChunkIndex,
                StepIndex = evt.StepIndex,
                StepType = evt.StepType,
                DomainType = evt.DomainType,
                TotalChunkCount = evt.TotalChunkCount,
                Delimiter = evt.Delimiter,
                UserId = evt.UserId,
                Status = status,
                ProcessedItemCount = response.SuccessCount,
                Message = response.Message,
                FailedIdentifiers = response.FailedIdentifiers,
                StartedAt = now.Add(-elapsed),
                CompletedAt = now,
                HandlerName = ConsumerType,
                RetryCount = 0,
                IsRetryable = false,
                ErrorCode = response.ErrorCode,
                ErrorMessage = response.ErrorMessage,
                ErrorDetail = response.ErrorDetail
            };

            return context.Publish(resultEvent, x => x.CorrelationId = evt.ChunkId);
        }

        /// <summary>
        /// Publish "compensation step completed" event for CompensationSaga.
        /// Correlation:
        ///   - CorrelationId uses CompensationId (NOT ChunkId) to group all compensation steps under one saga instance.
        /// </summary>
        private Task PublishCompensationStepCompletedEventAsync(ConsumeContext<TEvent> context)
        {
            var evt = context.Message;

            var compensationId = evt.CompensationId ?? Guid.Empty;
            var targetCount = evt.TargetIdentifiers?.Count ?? 0;

            var compensationEvent = new ChunkStepCompensationCompleteEvent
            {
                CompensationId = compensationId,
                JobId = evt.JobId,
                ChunkId = evt.ChunkId,
                Identifier = evt.Identifier,
                ChunkIndex = evt.ChunkIndex,
                DomainType = evt.DomainType,
                TotalChunkCount = evt.TotalChunkCount,
                Delimiter = evt.Delimiter,
                UserId = evt.UserId,
                StepIndex = evt.StepIndex,
                StepType = evt.StepType,
                CompensatedCount = targetCount,
                CompletedAt = DateTime.UtcNow,
                Message = $"Compensation completed for {targetCount} identifiers"
            };

            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Publish CompensationStepCompleted. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, CompensationId={CompensationId}, Count={Count}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, compensationId, targetCount);

            return context.Publish(compensationEvent, x => x.CorrelationId = compensationId);
        }

        /// <summary>
        /// Publish "compensation step failed" event for CompensationSaga.
        /// </summary>
        private Task PublishCompensationStepFailedEventAsync(ConsumeContext<TEvent> context, Exception ex)
        {
            var evt = context.Message;

            var compensationId = evt.CompensationId ?? Guid.Empty;

            var compensationFailedEvent = new ChunkStepCompensationFailEvent
            {
                CompensationId = compensationId,
                JobId = evt.JobId,
                ChunkId = evt.ChunkId,
                Identifier = evt.Identifier,
                ChunkIndex = evt.ChunkIndex,
                DomainType = evt.DomainType,
                TotalChunkCount = evt.TotalChunkCount,
                Delimiter = evt.Delimiter,
                UserId = evt.UserId,
                StepIndex = evt.StepIndex,
                StepType = evt.StepType,
                ErrorCode = ex.GetType().Name,
                ErrorMessage = ex.Message,
                ErrorDetail = ex.StackTrace,
                FailedAt = DateTime.UtcNow
            };

            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] Publish CompensationStepFailed. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, CompensationId={CompensationId}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, compensationId);

            return context.Publish(compensationFailedEvent, x => x.CorrelationId = compensationId);
        }

        // =========================================================================
        // Logging (override points)
        // =========================================================================

        protected virtual void LogPreExecution(TEvent evt)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Start step. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}, Identifier={Identifier}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.StepType, evt.Identifier);
        }

        protected virtual void LogPostExecution(TEvent evt, StepConsumerResponse response)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Step SUCCESS. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, SuccessCount={Count}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, response.SuccessCount);
        }

        protected virtual void LogPartialSuccess(TEvent evt, StepConsumerResponse response)
        {
            Logger.LogWarning(
                AppLog.Log("[{ConsumerType}] Step PARTIAL_SUCCESS. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, Success={Success}, Fail={Fail}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, response.SuccessCount, response.FailCount);
        }

        protected virtual void LogBusinessFail(TEvent evt, StepConsumerResponse response)
        {
            Logger.LogError(
                AppLog.Log("[{ConsumerType}] Step BUSINESS_FAIL. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, response.ErrorCode, response.ErrorMessage);
        }

        protected virtual void LogError(Exception ex, TEvent evt)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] Step EXCEPTION. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.StepType);
        }

        protected virtual void LogPreCompensation(TEvent evt)
        {
            var count = evt.TargetIdentifiers?.Count ?? 0;

            Logger.LogWarning(
                AppLog.Log("[{ConsumerType}] Start compensation. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}, CompensationId={CompensationId}, TargetCount={Count}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.StepType, evt.CompensationId, count);
        }

        protected virtual void LogPostCompensation(TEvent evt)
        {
            Logger.LogInformation(
                AppLog.Log("[{ConsumerType}] Compensation SUCCESS. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, CompensationId={CompensationId}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.CompensationId);
        }

        protected virtual void LogCompensationError(Exception ex, TEvent evt)
        {
            Logger.LogError(
                ex,
                AppLog.Log("[{ConsumerType}] Compensation FAILED. JobId={JobId}, ChunkId={ChunkId}, StepIndex={StepIndex}, CompensationId={CompensationId}"),
                ConsumerType, evt.JobId, evt.ChunkId, evt.StepIndex, evt.CompensationId);
        }

        // =========================================================================
        // Helper: convert external API response to step response
        // =========================================================================

        /// <summary>
        /// Converts RegisterResp to StepConsumerResponse.
        /// Notes:
        ///   - "00" => SUCCESS
        ///   - "98" => PARTIAL_SUCCESS (requires error list)
        ///   - "99" => BUSINESS_FAIL    (error list optional, but message should be meaningful)
        ///   - otherwise => throw BusinessException (caller will handle/publish as FAIL)
        /// </summary>
        protected StepConsumerResponse GetResult(RegisterResp resp)
        {
            if (resp is null) throw new ArgumentNullException(nameof(resp));

             switch (resp.resultCode)
            {
                case "00":
                    return StepConsumerResponse.Success(resp.succCount.GetValueOrDefault(), resp.resultMessage);

                case "98":
                    {
                        if (resp.error == null || resp.error.Count == 0)
                            throw new BusinessException($"{ConsumerType}: resultCode=98 but error list is empty.", resp.error);

                        var failedIdentifiers = resp.error.Select(x => x.apiColumn1)
                                               .Where(x => !string.IsNullOrWhiteSpace(x))
                                               .Distinct()
                                               .ToList();

                        var errorDetail = JsonConvert.SerializeObject(resp.error, JsonSettings);

                        return StepConsumerResponse.PartialSuccess(
                            resp.succCount.GetValueOrDefault(),
                            failedIdentifiers,
                            errorCode: resp.resultCode,
                            errorMessage: resp.resultMessage,
                            errorDetail: errorDetail);
                    }

                case "99":
                    {
                        // Business fail: allow missing error list, but keep detail if available.
                        var errorDetail = resp.error != null ? JsonConvert.SerializeObject(resp.error, JsonSettings) : string.Empty;

                        return StepConsumerResponse.BusinessFail(
                            resp.resultCode,
                            resp.resultMessage,
                            errorDetail);
                    }

                default:
                    // Unknown code => treat as business exception (consistent operational behavior).
                    throw new BusinessException(resp.resultMessage, resp.error);
            }
        }

        /// <summary>
        /// Filters out failed items from RequestPayload based on FailedIdentifiers.
        /// </summary>
        /// <param name="identifierProperties">Property names matching the composite key order</param>
        /// <param name="separator">Delimiter used in FailedIdentifiers (default: OrchestrationConstants.COMPOSITE_KEY_DELIMITER)</param>
        /// <returns>Filtered payload list excluding failed identifiers</returns>
        protected IList<TPayload> GetValidPayloads(
            string[] identifierProperties,
            char separator = OrchestrationConstants.COMPOSITE_KEY_SEPARATOR)
        {
            // No failures = return all
            if (FailedIdentifiers == null || FailedIdentifiers.Count == 0)
                return RequestPayload;

            // Build HashSet for O(1) lookup
            var failedSet = new HashSet<string>(FailedIdentifiers, StringComparer.Ordinal);

            var validPayloads = new List<TPayload>(RequestPayload.Count);

            foreach (var payload in RequestPayload)
            {
                var compositeKey = BuildCompositeKey(payload, identifierProperties, separator);

                if (!failedSet.Contains(compositeKey))
                {
                    validPayloads.Add(payload);
                }
            }

            return validPayloads;
        }

        /// <summary>
        /// Filters only failed items from RequestPayload based on FailedIdentifiers.
        /// </summary>
        /// <param name="identifierProperties">Property names matching the composite key order</param>
        /// <param name="separator">Delimiter used in FailedIdentifiers (default: OrchestrationConstants.COMPOSITE_KEY_SEPARATOR)</param>
        /// <returns>Filtered payload list containing only failed identifiers</returns>
        protected IList<TPayload> GetFailedPayloads(
            string[] identifierProperties,
            char separator = OrchestrationConstants.COMPOSITE_KEY_SEPARATOR)
        {
            // No failures = return empty
            if (FailedIdentifiers == null || FailedIdentifiers.Count == 0)
                return new List<TPayload>();

            // Build HashSet for O(1) lookup
            var failedSet = new HashSet<string>(FailedIdentifiers, StringComparer.Ordinal);

            var failedPayloads = new List<TPayload>(FailedIdentifiers.Count);

            foreach (var payload in RequestPayload)
            {
                var compositeKey = BuildCompositeKey(payload, identifierProperties, separator);

                if (failedSet.Contains(compositeKey))
                {
                    failedPayloads.Add(payload);
                }
            }

            return failedPayloads;
        }

        /// <summary>
        /// Builds composite key from payload using specified delimiter.
        /// </summary>
        private string BuildCompositeKey(TPayload payload, string[] identifierProperties, char separator)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < identifierProperties.Length; i++)
            {
                if (i > 0)
                    sb.Append(separator);

                var prop = typeof(TPayload).GetProperty(identifierProperties[i]);

                if (prop == null)
                {
                    throw new InvalidOperationException(
                        $"Property '{identifierProperties[i]}' not found on type '{typeof(TPayload).Name}'");
                }

                var value = prop.GetValue(payload)?.ToString()
                    ?? OrchestrationConstants.NULL_IDENTIFIER;

                sb.Append(value);
            }

            return sb.ToString();
        }
    }
}
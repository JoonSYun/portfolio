using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    // ================================================================
    // 1) Step Interfaces
    // ================================================================

    /// <summary>Step Event marker</summary>
    public interface IStepEvent : IStepScope { }

    // ================================================================
    // 2) Abstract Base Classes
    // ================================================================

    /// <summary>
    /// Step message base
    /// - Common properties for all Step-related messages
    /// </summary>
    public abstract class StepMessageBase : IStepScope
    {
        // Job scope (from IJobScope)
        public Guid JobId { get; set; }
        public string JobIdentifier { get; set; } = string.Empty;
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string Delimiter { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        // Chunk scope (from IChunkScope)
        public Guid ChunkId { get; set; }
        public string? Identifier { get; set; }
        public int ChunkIndex { get; set; }

        // Step scope (from IStepScope)
        public int StepIndex { get; set; }
        public string StepType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Step execution context base
    /// - Contains execution info that Consumer receives
    /// </summary>
    public abstract class StepExecutionBase : StepMessageBase, IStepExecutionContext
    {
        public string PayloadJson { get; set; } = string.Empty;
    }

    /// <summary>
    /// Step result base
    /// - Common properties for SUCCESS/PARTIAL_SUCCESS/FAIL results
    /// </summary>
    public abstract class StepResultBase : StepMessageBase, IStepResult, ITimestamped
    {
        public string Status { get; set; } = string.Empty;
        public string BusinessStatus { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string HandlerName { get; set; } = string.Empty;
        public int ProcessedItemCount { get; set; }
        public string? Message { get; set; }
        public List<string>? FailedIdentifiers { get; set; }
        public int RetryCount { get; set; }
        public bool IsRetryable { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
    }

    // ================================================================
    // 3) Step Init Event
    // ================================================================

    /// <summary>Step initialization event (Saga start)</summary>
    public class ChunkStepInitEvent : StepMessageBase
    {
        public int Priority { get; set; }
        public string RequestBrandCode { get; set; } = string.Empty;
        public string SlipDiv { get; set; } = string.Empty;
    }

    // ================================================================
    // 4) Step Result Event (Unified)
    // ================================================================

    /// <summary>
    /// Step execution result event (unified)
    /// - Handles SUCCESS / PARTIAL_SUCCESS / FAILED
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ChunkStepResultEvent : StepResultBase, IStepEvent
    {
        public ChunkStepResultEvent()
        {
            Status = ChunkScopeStatus.PENDING.ToString();
        }
    }

    // ================================================================
    // 5) Step Domain Event Base (actual type that Consumer receives)
    // ================================================================

    /// <summary>
    /// Step execution event base
    /// - Domain-specific Consumer inherits and implements
    /// - Created dynamically via Reflection
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public abstract class ChunkStepDomainEventBase : StepExecutionBase, IStepEvent
    {
        // Subclasses can define additional properties
        // e.g., ValidateOrderStepEvent, CreateOrderStepEvent, etc.
        public string? RequestBrandCode { get; set; }

        public string? PartialFailureDetailsJson { get; set; }

        /// <summary>
        /// ⭐ Compensation mode flag
        /// - false: Normal execution (process entire PayloadJson)
        /// - true: Compensation execution (process only TargetIdentifiers)
        /// </summary>
        public bool IsCompensation { get; set; }

        /// <summary>
        /// Target identifiers for compensation
        /// - Used only when IsCompensation = true
        /// </summary>
        public List<string> TargetIdentifiers { get; set; } = new();

        /// <summary>
        /// ⭐ Compensation Saga ID (NEW)
        /// - Set only when IsCompensation = true
        /// - Used for communication with CompensationSaga
        /// </summary>
        public Guid? CompensationId { get; set; }
    }

    /// <summary>
    /// Step Consumer response model
    /// 
    /// Information that developer must return:
    /// 1. Result type (Success/PartialSuccess/BusinessFail)
    /// 2. Success/fail counts
    /// 3. Failed identifiers (for PartialSuccess)
    /// 4. Error info (for PartialSuccess/BusinessFail)
    /// </summary>
    public class StepConsumerResponse
    {
        /// <summary>Processing result type</summary>
        public StepResultType ResultType { get; set; }

        /// <summary>Success processing count</summary>
        public int SuccessCount { get; set; }

        /// <summary>Fail processing count</summary>
        public int FailCount { get; set; }

        /// <summary>
        /// Failed identifiers list (required for PartialSuccess)
        /// 
        /// Example:
        /// - Out of 48, 45 succeeded, 3 failed
        /// - FailedIdentifiers = ["ORD-001", "ORD-002", "ORD-003"]
        /// </summary>
        public List<string> FailedIdentifiers { get; set; } = new();

        /// <summary>Success message (optional)</summary>
        public string? Message { get; set; }

        /// <summary>Error code (for PartialSuccess/BusinessFail)</summary>
        public string? ErrorCode { get; set; }

        /// <summary>Error message (for PartialSuccess/BusinessFail)</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Error detail (for PartialSuccess/BusinessFail)</summary>
        public string? ErrorDetail { get; set; }

        // ========== Factory Methods ==========

        /// <summary>
        /// Full success
        /// 
        /// Usage example:
        /// return StepConsumerResponse.Success(processedCount, "Order validation completed");
        /// </summary>
        public static StepConsumerResponse Success(int successCount, string? message = null)
        {
            return new StepConsumerResponse
            {
                ResultType = StepResultType.SUCCESS,
                SuccessCount = successCount,
                FailCount = 0,
                Message = message
            };
        }

        /// <summary>
        /// Partial success (some failures)
        /// 
        /// Usage example:
        /// return StepConsumerResponse.PartialSuccess(
        ///     successCount: 45,
        ///     failedIdentifiers: ["ORD-001", "ORD-002", "ORD-003"],
        ///     errorCode: "PARTIAL_INVENTORY_FAIL",
        ///     errorMessage: "Some items out of stock",
        ///     errorDetail: JsonConvert.SerializeObject(failedItems)
        /// );
        /// </summary>
        public static StepConsumerResponse PartialSuccess(
            int successCount,
            List<string> failedIdentifiers,
            string? errorCode = null,
            string? errorMessage = null,
            string? errorDetail = null)
        {
            return new StepConsumerResponse
            {
                ResultType = StepResultType.PARTIAL_SUCCESS,
                SuccessCount = successCount,
                FailCount = failedIdentifiers?.Count ?? 0,
                FailedIdentifiers = failedIdentifiers ?? new List<string>(),
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ErrorDetail = errorDetail
            };
        }

        /// <summary>
        /// Business failure (flow interruption)
        /// 
        /// Usage example:
        /// return StepConsumerResponse.BusinessFail(
        ///     errorCode: "INVALID_ORDER",
        ///     errorMessage: "Order status is invalid",
        ///     errorDetail: ex.StackTrace
        /// );
        /// </summary>
        public static StepConsumerResponse BusinessFail(
            string errorCode,
            string errorMessage,
            string? errorDetail = null)
        {
            return new StepConsumerResponse
            {
                ResultType = StepResultType.BUSINESS_FAIL,
                SuccessCount = 0,
                FailCount = 0,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ErrorDetail = errorDetail
            };
        }
    }
}
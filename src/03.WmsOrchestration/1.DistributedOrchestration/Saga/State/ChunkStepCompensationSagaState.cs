using MassTransit;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// ChunkStep Compensation Saga State
    /// </summary>
    public class ChunkStepCompensationSagaState : SagaStateMachineInstance, IChunkIdentifierSaga, ITimestampedSaga, IErrorTrackingSaga
    {
        // ISagaState
        public Guid CorrelationId { get; set; }
        public string? CurrentState { get; set; }
        public int Version { get; set; }

        // IChunkIdentifierSaga (includes IJobContextSaga)
        public Guid JobId { get; set; }
        public Guid ChunkId { get; set; }
        public string? Identifier { get; set; }
        public int ChunkIndex { get; set; }
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string? Delimiter { get; set; }
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
        public string UserId { get; set; } = string.Empty;

        // ITimestampedSaga
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        // IErrorTrackingSaga
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }

        // Compensation-specific
        public Guid CompensationId { get; set; }
        public string CompensationPlanJson { get; set; } = "[]";
        public int TotalCompensationSteps { get; set; }
        public int CurrentCompensationIndex { get; set; }
        public string FailedIdentifiersJson { get; set; } = "[]";
        public string PayloadJson { get; set; } = string.Empty;
        public string CompensatedStepIndexesJson { get; set; } = "[]";
        public int CompensatedStepCount { get; set; }
        public int? FailedStepIndex { get; set; }
        public string? FailedStepType { get; set; }
        public DateTime? FailedAt { get; set; }
    }
}
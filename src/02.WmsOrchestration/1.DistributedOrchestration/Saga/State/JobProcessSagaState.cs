using MassTransit;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Job Process Saga State
    /// </summary>
    public class JobProcessSagaState : SagaStateMachineInstance, IJobContextSaga, ITimestampedSaga
    {
        // ISagaState
        public Guid CorrelationId { get; set; }
        public string? CurrentState { get; set; }
        public int Version { get; set; }

        // IJobContextSaga
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string? Delimiter { get; set; }
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
        public string UserId { get; set; } = string.Empty;

        // ITimestampedSaga
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // JobProcess-specific
        public string JobIdentifier { get; set; } = string.Empty;
        public string JobStatus { get; set; } = string.Empty;
        public string ProcessPlanJson { get; set; } = "[]";
        public string PayloadJson { get; set; } = string.Empty;
        public int TotalProcessCount { get; set; }
        public int CurrentProcessIndex { get; set; }
        public string ExecutedProcessesJson { get; set; } = "[]";
        public int CompletedProcessCount { get; set; }
        public int FailedProcessCount { get; set; }
        public int? FailedProcessIndex { get; set; }
    }
}
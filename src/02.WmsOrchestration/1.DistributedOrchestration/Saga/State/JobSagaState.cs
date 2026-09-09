using MassTransit;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Job-level saga state.
    /// </summary>
    public class JobSagaState : SagaStateMachineInstance, IJobContextSaga, IJobIdentifierSaga
    {
        // ISagaState
        public Guid CorrelationId { get; set; }
        public string? CurrentState { get; set; }
        public int Version { get; set; }

        // IJobIdentifierSaga
        public string JobIdentifier { get; set; } = string.Empty;
        public string JobIdentifierType { get; set; } = string.Empty;

        // IJobContextSaga
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string? Delimiter { get; set; }
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
        public string UserId { get; set; } = string.Empty;

        // Job-specific
        public bool SignalR_YN { get; set; }
        public bool IsBypass { get; set; }
        public string? ChunkStatus { get; set; }
        public int ChunkSuccessCount { get; set; }
        public int ChunkPartialSuccessCount { get; set; }
        public int ChunkFailedCount { get; set; }
        public string? JobProcessStatus { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime SuspendedAt { get; set; }
        public DateTime CompletedAt { get; set; }
    }
}
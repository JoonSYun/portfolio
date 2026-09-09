using MassTransit;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.WmsOrchestration.Saga
{
    public class ChunkStepSagaState : SagaStateMachineInstance, IChunkIdentifierSaga, ITimestampedSaga, IErrorTrackingSaga
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

        // ChunkStep-specific
        public string PayloadJson { get; set; } = string.Empty;
        public string StepPlanJson { get; set; } = "[]";
        public int TotalStepCount { get; set; }
        public int CurrentStepIndex { get; set; }
        public string PendingCompensationsJson { get; set; } = "[]";

        private PendingCompensationTracker? _pendingCompensationsTracker;

        [NotMapped]
        public PendingCompensationTracker PendingCompensations
        {
            get
            {
                if (_pendingCompensationsTracker == null)
                {
                    _pendingCompensationsTracker = new PendingCompensationTracker(PendingCompensationsJson);
                }
                return _pendingCompensationsTracker;
            }
        }

        public void SyncPendingCompensationsToJson()
        {
            if (_pendingCompensationsTracker != null)
            {
                PendingCompensationsJson = _pendingCompensationsTracker.ToJson();
            }
        }

        public bool HasPartialSuccess { get; set; }
        public bool IsStepFailed { get; set; }
        public string PartialFailureDetailsJson { get; set; } = "{}";
        public int ProcessedItemCount { get; set; }
        public int TotalFailCount { get; set; }
    }

    public class PendingCompensationTracker
    {
        private readonly List<Guid> _compensations;

        public PendingCompensationTracker(string json)
        {
            try
            {
                _compensations = JsonConvert.DeserializeObject<List<Guid>>(json)
                    ?? new List<Guid>();
            }
            catch (JsonException)
            {
                _compensations = new List<Guid>();
            }
        }

        public PendingCompensationTracker()
        {
            _compensations = new List<Guid>();
        }

        public void Add(Guid compensationId)
        {
            if (compensationId == Guid.Empty)
            {
                return;
            }

            if (!_compensations.Contains(compensationId))
            {
                _compensations.Add(compensationId);
            }
        }

        public bool Remove(Guid compensationId)
        {
            return _compensations.Remove(compensationId);
        }

        public bool Contains(Guid compensationId)
        {
            return _compensations.Contains(compensationId);
        }

        public bool IsEmpty => !_compensations.Any();
        public int Count => _compensations.Count;
        public IReadOnlyList<Guid> Compensations => _compensations.AsReadOnly();

        public string ToJson()
        {
            return JsonConvert.SerializeObject(_compensations);
        }

        public override string ToString()
        {
            return $"PendingCompensations: Count={Count}, IsEmpty={IsEmpty}";
        }
    }
}
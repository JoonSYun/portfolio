using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    // ================================================================
    // 1) Chunk Interfaces
    // ================================================================

    /// <summary>
    /// Chunk Message 마커
    /// - JobSaga와 통신하는 Chunk 메시지
    /// </summary>
    public interface IChunkMessage : IChunkScope, IJobSagaEvent { }

    /// <summary>Chunk Event 마커</summary>
    public interface IChunkEvent : IChunkMessage
    {
        string EventType => GetType().Name;
    }

    /// <summary>Chunk Update Command 마커</summary>
    public interface IChunkUpdateCommand : IChunkMessage
    {
        string EventType => GetType().Name;
    }

    // ================================================================
    // 2) Abstract Base Classes
    // ================================================================

    /// <summary>
    /// Chunk 메시지 추상 베이스
    /// - 모든 Chunk 관련 메시지의 공통 속성 구현
    /// </summary>
    public abstract class ChunkMessageBase : IChunkScope
    {
        // Job scope
        public Guid JobId { get; set; }
        public string JobIdentifier { get; set; } = string.Empty;
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string Delimiter { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        // Chunk scope
        public Guid ChunkId { get; set; }
        public string? Identifier { get; set; }
        public int ChunkIndex { get; set; }
    }

    /// <summary>
    /// Chunk Event 추상 베이스
    /// - EventType 자동 구현
    /// </summary>
    public abstract class ChunkEventBase : ChunkMessageBase, IChunkEvent
    {
        public string EventType => GetType().Name;
    }

    /// <summary>
    /// Chunk 결과 추상 베이스
    /// </summary>
    public abstract class ChunkResultBase : ChunkEventBase, IChunkStatus, ITimestamped
    {
        public string Status { get; set; } = string.Empty;
        public string BusinessStatus { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string HandlerName { get; set; } = string.Empty;
        public int ProcessedItemCount { get; set; }
        public string? Message { get; set; }
        public int RetryCount { get; set; }
        public bool IsRetryable { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
    }

    // ================================================================
    // 3) Chunk Result Event (통합)
    // ================================================================

    /// <summary>
    /// Chunk 처리 결과 이벤트 (통합)
    /// - COMPLETED / PARTIAL_SUCCESS / FAILED 모두 처리
    /// - ChunkStepSaga가 결정한 Status를 신뢰
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ChunkResultEvent : ChunkResultBase
    {
    }

    // ================================================================
    // 4) Chunk Update Command (통합)
    // ================================================================

    /// <summary>
    /// Chunk 결과 업데이트 커맨드 (통합)
    /// - COMPLETED / PARTIAL_SUCCESS / FAILED 모두 처리
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ChunkResultUpdateCommand : ChunkResultBase, IChunkUpdateCommand
    {
    }
}
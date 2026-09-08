namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Saga 기본 인터페이스
    /// </summary>
    public interface ISagaState
    {
        Guid CorrelationId { get; set; }
        string? CurrentState { get; set; }
        int Version { get; set; }
    }

    /// <summary>
    /// Job 컨텍스트 정보를 가진 Saga
    /// </summary>
    public interface IJobContextSaga : ISagaState
    {
        string DomainType { get; set; }
        int TotalChunkCount { get; set; }
        string? Delimiter { get; set; }
        string? RequestBrandCode { get; set; }
        string? SlipDiv { get; set; }
        string UserId { get; set; }
    }

    /// <summary>
    /// Chunk 식별 정보를 가진 Saga
    /// </summary>
    public interface IChunkIdentifierSaga : IJobContextSaga
    {
        Guid JobId { get; set; }
        Guid ChunkId { get; set; }
        string? Identifier { get; set; }
        int ChunkIndex { get; set; }
    }

    /// <summary>
    /// 타임스탬프를 가진 Saga
    /// </summary>
    public interface ITimestampedSaga
    {
        DateTime CreatedAt { get; set; }
        DateTime UpdatedAt { get; set; }
        DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// 에러 정보를 가진 Saga
    /// </summary>
    public interface IErrorTrackingSaga
    {
        string? ErrorCode { get; set; }
        string? ErrorMessage { get; set; }
        string? ErrorDetail { get; set; }
    }

    /// <summary>
    /// Job 식별자를 가진 Saga
    /// </summary>
    public interface IJobIdentifierSaga
    {
        string JobIdentifier { get; set; }
        string JobIdentifierType { get; set; }
    }
}
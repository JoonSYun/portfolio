namespace Portfolio.WmsOrchestration.Saga
{
    // ================================================================
    // 1) Core Timing & Error Interfaces (변경 없음)
    // ================================================================

    /// <summary>타임스탬프 공통 속성</summary>
    public interface ITimestamped
    {
        DateTime StartedAt { get; set; }
        DateTime CompletedAt { get; set; }
    }

    /// <summary>에러 정보 공통 속성</summary>
    public interface IErrorInfo
    {
        string? ErrorCode { get; set; }
        string? ErrorMessage { get; set; }
        string? ErrorDetail { get; set; }
    }

    /// <summary>재시도 관련 공통 속성</summary>
    public interface IRetryable
    {
        int RetryCount { get; set; }
        bool IsRetryable { get; set; }
    }

    /// <summary>처리 아이템 카운트 공통 속성</summary>
    public interface IProcessable
    {
        int ProcessedItemCount { get; set; }
    }

    // ================================================================
    // 2) Status Interfaces (개선)
    // ================================================================

    /// <summary>상태 공통 속성 - 모든 레벨에서 사용</summary>
    public interface IStatusInfo
    {
        string Status { get; set; }
    }

    /// <summary>비즈니스 상태 공통 속성</summary>
    public interface IBusinessStatus
    {
        string BusinessStatus { get; set; }
    }

    /// <summary>Job 상태 (호환성 유지)</summary>
    public interface IJobStatus : IStatusInfo, IBusinessStatus { }

    /// <summary>Chunk 상태 (호환성 유지)</summary>
    public interface IChunkStatus : IStatusInfo, IBusinessStatus { }

    // ================================================================
    // 3) 계층 구조 Core Interfaces (NEW)
    // ================================================================

    /// <summary>
    /// Job 레벨 공통 속성
    /// - 모든 Job 관련 메시지의 최상위 인터페이스
    /// </summary>
    public interface IJobScope
    {
        Guid JobId { get; set; }
        public string JobIdentifier { get; set; }
        string DomainType { get; set; }
        int TotalChunkCount { get; set; }
        string Delimiter { get; set; }
        string UserId { get; set; }
    }

    /// <summary>
    /// Chunk 레벨 공통 속성
    /// - Job 속성 + Chunk 식별 정보
    /// </summary>
    public interface IChunkScope : IJobScope
    {
        Guid ChunkId { get; set; }
        string? Identifier { get; set; }
        int ChunkIndex { get; set; }
    }

    /// <summary>
    /// Process 레벨 공통 속성
    /// - Job 속성 + Process 식별 정보
    /// </summary>
    public interface IProcessScope : IJobScope
    {
        Guid ProcessId { get; set; }
        int ProcessIndex { get; set; }
        string ProcessType { get; set; }
    }

    /// <summary>
    /// Step 레벨 공통 속성
    /// - Chunk 속성 + Step 식별 정보
    /// </summary>
    public interface IStepScope : IChunkScope
    {
        int StepIndex { get; set; }
        string StepType { get; set; }
    }

    // ================================================================
    // 4) Context Interfaces (실행 컨텍스트)
    // ================================================================

    /// <summary>
    /// Process 실행 컨텍스트
    /// - Process Consumer가 받는 실행 정보
    /// </summary>
    public interface IProcessExecutionContext : IProcessScope
    {
        string JobStatus { get; set; }
        string? RequestBrandCode { get; set; }
        string? SlipDiv { get; set; }
    }

    /// <summary>
    /// Step 실행 컨텍스트
    /// - Step Consumer가 받는 실행 정보
    /// </summary>
    public interface IStepExecutionContext : IStepScope
    {
        string PayloadJson { get; set; }
    }

    // ================================================================
    // 5) Result Interfaces (결과 이벤트)
    // ================================================================

    /// <summary>Process 결과 이벤트 마커</summary>
    public interface IProcessResult : IProcessScope, IStatusInfo { }

    /// <summary>Step 결과 이벤트 마커</summary>
    public interface IStepResult : IStepScope, IStatusInfo, IBusinessStatus { }
}
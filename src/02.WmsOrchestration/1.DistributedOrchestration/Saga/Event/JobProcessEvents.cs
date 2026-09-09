using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    // ================================================================
    // 1) Process Event Interfaces (간소화)
    // ================================================================

    /// <summary>Process Event 마커</summary>
    public interface IProcessEvent : IProcessScope { }

    // ================================================================
    // 2) Abstract Base Classes (NEW)
    // ================================================================

    /// <summary>
    /// Process 메시지 추상 베이스
    /// - 모든 Process 관련 메시지의 공통 속성 구현
    /// </summary>
    public abstract class ProcessMessageBase : IProcessScope
    {
        public Guid JobId { get; set; }
        public string JobIdentifier { get; set; } = string.Empty;
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string Delimiter { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        public Guid ProcessId { get; set; }
        public int ProcessIndex { get; set; }
        public string ProcessType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Process 실행 컨텍스트 추상 베이스
    /// - Consumer가 받는 실행 정보 포함
    /// </summary>
    public abstract class ProcessExecutionBase : ProcessMessageBase, IProcessExecutionContext
    {
        public string JobStatus { get; set; } = string.Empty;
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
    }

    /// <summary>
    /// Process 결과 이벤트 추상 베이스
    /// - 공통 타이밍 정보 포함
    /// </summary>
    public abstract class ProcessResultBase : ProcessMessageBase, IProcessResult, ITimestamped
    {
        public string Status { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string? Message { get; set; }
    }

    // ================================================================
    // 3) Process Init Event (기존 이름 유지)
    // ================================================================

    /// <summary>Job Process 초기화 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobProcessInitEvent : JobMessageBase
    {
        public string JobStatus { get; set; } = string.Empty;
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
    }

    // ================================================================
    // 4) Process Start Event (기존 이름 유지, 상속 변경)
    // ================================================================

    /// <summary>Process 시작 이벤트 (사용 안 함 - 삭제 고려)</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobProcessStartEvent : ProcessExecutionBase, IProcessEvent
    {
        // NOTE: 현재 사용되지 않음 - JobProcessDomainEventBase가 실제 사용됨
    }

    // ================================================================
    // 5) Process Result Events (기존 이름 유지, 상속 변경)
    // ================================================================

    /// <summary>Process 완료 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ProcessCompleteEvent : ProcessResultBase
    {
        // Status는 base에서 처리
    }

    /// <summary>Process 실패 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ProcessFailEvent : ProcessResultBase, IErrorInfo, IRetryable
    {
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
        public int RetryCount { get; set; }
        public bool IsRetryable { get; set; }
    }

    // ================================================================
    // 6) Process Complete Event (JobSaga로 전달)
    // ================================================================

    /// <summary>모든 Process 완료 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class AllJobProcessCompleteEvent : JobCompleteBase
    {
        public string EventType => GetType().Name;
        public int TotalProcessCount { get; set; }
        public bool AllSuccess { get; set; }
        public int? FailedProcessIndex { get; set; }
    }

    // ================================================================
    // 7) Process Domain Event Base (실제 Consumer가 받는 타입)
    // ================================================================

    /// <summary>
    /// Process 실행 이벤트 추상 베이스
    /// - 도메인별 Consumer가 상속받아 구현
    /// - Reflection으로 동적 생성됨
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public abstract class JobProcessDomainEventBase : ProcessExecutionBase, IProcessEvent
    {
        // 하위 클래스에서 추가 속성 정의 가능
        // 예: StandardAPIProcessEvent, ERPNotifyProcessEvent 등
        public string? PayloadJson { get; set; } = string.Empty;
    }
}
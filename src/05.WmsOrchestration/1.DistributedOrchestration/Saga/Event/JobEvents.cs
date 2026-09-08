using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    // ================================================================
    // 1) Job Event Interfaces (간소화)
    // ================================================================

    /// <summary>
    /// Job Saga Event 마커
    /// - JobSaga가 처리하는 모든 메시지의 기본 인터페이스
    /// - Job, Chunk, Process 메시지 모두 포함
    /// </summary>
    public interface IJobSagaEvent : IJobScope
    {
        string EventType => GetType().Name;
    }

    /// <summary>Job Event 마커 - IJobSagaEvent의 하위 타입</summary>
    public interface IJobEvent : IJobSagaEvent { }

    /// <summary>Job Update Command 마커</summary>
    public interface IJobUpdateCommand : IJobScope
    {
        string EventType => GetType().Name;
    }

    /// <summary>Job Suspense 공통 속성</summary>
    public interface IJobSuspense
    {
        string IdempotencyKey { get; set; }
        string SuspenseType { get; set; }
        string CreatedBy { get; set; }
        string SuspenseReason { get; set; }
    }

    // ================================================================
    // 2) Abstract Base Classes (NEW - 공통 로직 추상화)
    // ================================================================

    /// <summary>
    /// Job 메시지 추상 베이스
    /// - 모든 Job 관련 메시지의 공통 속성 구현
    /// </summary>
    public abstract class JobMessageBase : IJobScope
    {
        public Guid JobId { get; set; }
        public string JobIdentifier { get; set; } = string.Empty;
        public string DomainType { get; set; } = string.Empty;
        public int TotalChunkCount { get; set; }
        public string Delimiter { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Job Event 추상 베이스
    /// - EventType 자동 구현
    /// </summary>
    public abstract class JobEventBase : JobMessageBase, IJobEvent
    {
        public string EventType => GetType().Name;
        public bool IsBypass { get; set; } = false;
    }

    /// <summary>
    /// Job Complete 추상 베이스
    /// - Complete 이벤트/커맨드 공통 속성
    /// </summary>
    public abstract class JobCompleteBase : JobEventBase, IJobStatus
    {
        public string Status { get; set; } = string.Empty;
        public string BusinessStatus { get; set; } = string.Empty;
    }

    /// <summary>
    /// Job Suspense 추상 베이스
    /// - Suspense 이벤트/커맨드 공통 속성
    /// </summary>
    public abstract class JobSuspenseBase : JobEventBase, IJobSuspense
    {
        public string IdempotencyKey { get; set; } = string.Empty;
        public string SuspenseType { get; set; } = JobSuspenseType.CANCELED.ToString();
        public string CreatedBy { get; set; } = string.Empty;
        public string SuspenseReason { get; set; } = string.Empty;
    }

    // ================================================================
    // 3) Job Events (기존 이름 유지, 상속만 변경)
    // ================================================================

    /// <summary>Job 시작 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobStartEvent : JobEventBase
    {
        public bool SignalR_YN { get; set; }
        public string JobIdentifierType { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string RequestBrandCode { get; set; } = string.Empty;
        public string SlipDiv { get; set; } = string.Empty;
    }

    /// <summary>Job 완료 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class AllChunkCompleteEvent : JobCompleteBase 
    {
        public AllChunkResult_DTO? ChunkResult { get; set; }
    }

    /// <summary>Job 중단 이벤트</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobSuspendedEvent : JobSuspenseBase { }

    // ================================================================
    // 4) Job Update Commands (기존 이름 유지, 상속만 변경)
    // ================================================================

    /// <summary>Job 완료 업데이트 커맨드</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobCompleteUpdateCommand : JobCompleteBase, IJobUpdateCommand
    {
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
        public int ChunkSuccessCount { get; set; }
        public int ChunkPartialSuccessCount { get; set; }
        public int ChunkFailedCount { get; set; }
    }

    /// <summary>Job 중단 업데이트 커맨드</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobSuspendedUpdateCommand : JobSuspenseBase, IJobUpdateCommand { }

    // ================================================================
    // 5) Job Commands (기존 유지)
    // ================================================================

    /// <summary>Job 발행 커맨드</summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class JobPublishCommand : JobMessageBase, IJobSagaEvent
    {
        public string EventType => GetType().Name;
        public int Priority { get; set; }
        public string RequestBrandCode { get; set; } = string.Empty;
        public string SlipDiv { get; set; } = string.Empty;
    }
}
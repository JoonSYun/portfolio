using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    // ================================================================
    // 1) Compensation Saga Scope Interface
    // ================================================================

    /// <summary>
    /// Compensation Saga 범위 인터페이스
    /// </summary>
    public interface ICompensationScope : IChunkScope
    {
        Guid CompensationId { get; set; }
    }

    // ================================================================
    // 2) Compensation Event Interfaces
    // ================================================================

    /// <summary>Compensation Event 마커</summary>
    public interface ICompensationEvent : ICompensationScope
    {
        string EventType => GetType().Name;
    }

    // ================================================================
    // 3) Abstract Base Classes
    // ================================================================

    /// <summary>
    /// Compensation 메시지 추상 베이스
    /// </summary>
    public abstract class CompensationMessageBase : ICompensationScope
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

        // Compensation scope
        public Guid CompensationId { get; set; }
    }

    /// <summary>
    /// Compensation Event 추상 베이스
    /// </summary>
    public abstract class CompensationEventBase : CompensationMessageBase, ICompensationEvent
    {
        public string EventType => GetType().Name;
    }

    // ================================================================
    // 4) Compensation Init Event
    // ================================================================

    /// <summary>
    /// Compensation Saga 시작 이벤트
    /// 
    /// ChunkStepSaga에서 PartialSuccess 발생 시 발행
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ChunkStepCompensationInitEvent : CompensationEventBase
    {
        /// <summary>
        /// 보상할 Step 계획 (JSON)
        /// List&lt;CompensationStepInfo&gt; 직렬화
        /// 
        /// 예시:
        /// [
        ///   { "StepIndex": 1, "StepType": "AllocateInventoryStepEvent" },
        ///   { "StepIndex": 0, "StepType": "ValidateOrderStepEvent" }
        /// ]
        /// 
        /// ⭐ 역순으로 정렬되어 있음 (Step1 → Step0)
        /// </summary>
        public string CompensationPlanJson { get; set; } = "[]";

        /// <summary>보상 대상 식별자 목록</summary>
        public List<string> FailedIdentifiers { get; set; } = new();

        /// <summary>PayloadJson (보상 실행에 필요)</summary>
        public string PayloadJson { get; set; } = string.Empty;

        /// <summary>컨텍스트 정보</summary>
        public string? RequestBrandCode { get; set; }
        public string? SlipDiv { get; set; }
    }

    // ================================================================
    // 5) Compensation Step Result Events
    // ================================================================

    /// <summary>
    /// 보상 Step 완료 이벤트
    /// 
    /// Consumer에서 보상 완료 시 발행
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ChunkStepCompensationCompleteEvent : CompensationEventBase
    {
        public int StepIndex { get; set; }
        public string StepType { get; set; } = string.Empty;
        public int CompensatedCount { get; set; }
        public DateTime CompletedAt { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// 보상 Step 실패 이벤트
    /// 
    /// Consumer에서 보상 실패 시 발행
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class ChunkStepCompensationFailEvent : CompensationEventBase, IErrorInfo
    {
        public int StepIndex { get; set; }
        public string StepType { get; set; } = string.Empty;
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
        public DateTime FailedAt { get; set; }
    }

    // ================================================================
    // 6) Compensation Complete Events
    // ================================================================

    /// <summary>
    /// 전체 보상 완료 이벤트
    /// 
    /// 모든 보상 Step이 성공적으로 완료됨
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class CompensationCompleteEvent : CompensationEventBase
    {
        public int TotalCompensatedSteps { get; set; }
        public DateTime CompletedAt { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// 보상 실패 알림 이벤트
    /// 
    /// 보상 중 실패 발생 → 개발자 개입 필요
    /// </summary>
    [MassTransitAutoBind(nameof(ConsumerBindingType.JOB_SAGA_EVENT))]
    public class CompensationFailEventBase : CompensationEventBase, IErrorInfo
    {
        public int FailedStepIndex { get; set; }
        public string FailedStepType { get; set; } = string.Empty;
        public List<string> FailedIdentifiers { get; set; } = new();
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
        public DateTime FailedAt { get; set; }

        /// <summary>
        /// 보상 완료한 Step 목록 (실패 이전까지)
        /// </summary>
        public List<int> CompensatedStepIndexes { get; set; } = new();

        /// <summary>
        /// 보상하지 못한 Step 목록 (실패 이후)
        /// </summary>
        public List<int> SkippedStepIndexes { get; set; } = new();
    }

    public class CompensationFailEvent : CompensationFailEventBase { }

    public class CompensationFailAlertEvent : CompensationFailEventBase { }

    // ================================================================
    // 7) Compensation Plan Info
    // ================================================================

    /// <summary>
    /// 보상 Step 정보
    /// </summary>
    public class CompensationStepInfo
    {
        /// <summary>Step Index (0-based)</summary>
        public int StepIndex { get; set; }

        /// <summary>Step Type (Reflection용)</summary>
        public string StepType { get; set; } = string.Empty;
    }
}
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// ChunkStep 실행 계획 정보
    /// StepPlanJson에 직렬화되어 저장됨
    /// </summary>
    public class ChunkStepPlanInfo
    {
        /// <summary>Step 타입 (DTO 클래스명)</summary>
        public string StepType { get; set; } = string.Empty;

        /// <summary>Step 순서 (1부터 시작)</summary>
        public int Index { get; set; }

        public StepStartConditionType StartCondition { get; set; }

        /// <summary>Step 설명 (로깅용)</summary>
        public string? Description { get; set; }
    }
}

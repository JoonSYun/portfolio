using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Model
{
    public interface IChunkPayload : IChunkMessage
    {
        public Guid ChunkId { get; set; }
        public Guid JobId { get; set; }
        public string DomainType { get; set; }
        public string Identifier { get; set; }
        public int TotalChunkCount { get; set; }
        public int ChunkIndex { get; set; }
        public int ItemCount { get; set; }
        public string HandlerName { get; set; }
        public string UserId { get; set; }
        public bool IsSynchronous { get; set; }
        public string Delimiter { get; set; }

        // ==========================
        // RETRY ORCHESTRATION FIELDS
        // ==========================
        // 2025-12-22
        // - Add HandlerType to support different chunk handlers (e.g., DomainController, RetryScheduler, etc.)
        // - This filed will be used to fork rethrive Job Saga Enable in ConsumerBase
        public string HandlerType { get; set; }
        // ==========================
        // STEP ORCHESTRATION FIELDS
        // ==========================

        /// <summary> 현재 실행해야 할 Step 번호 (1,2,3,...)</summary>
        public int StepIndex { get; set; }

        /// <summary> Step 이름 (ChunkStepAttribute 기반 StepType.Name) </summary>
        public string StepName { get; set; }

        /// <summary> Step 실행을 위한 DTO 타입 이름 </summary>
        public string PayloadTypeName { get; set; }
    }
}

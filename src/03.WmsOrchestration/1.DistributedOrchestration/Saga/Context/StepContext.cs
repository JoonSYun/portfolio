using MassTransit;
using Microsoft.Extensions.Logging;

namespace Portfolio.WmsOrchestration.Saga
{
    public class StepContext<T>
    {
        public Guid JobId { get; set; }
        public Guid ChunkId { get; set; }
        public string Identifier { get; set; }

        // ===== Step 정보 =====
        public int StepIndex { get; set; }
        public string StepName { get; set; }

        // 병렬 step 그룹을 위한 값(필요시)
        public int GroupIndex { get; set; }

        // ===== ChunkPayload_Message 기반 정보 =====
        public int TotalChunkCount { get; set; }
        public int ChunkIndex { get; set; }
        public int ItemCount { get; set; }
        public string HandlerName { get; set; }
        public bool IsSynchronous { get; set; }
        public string UserId { get; set; }

        // 실제 Step 실행할 데이터
        public IEnumerable<T> Payload { get; set; }
    }
}

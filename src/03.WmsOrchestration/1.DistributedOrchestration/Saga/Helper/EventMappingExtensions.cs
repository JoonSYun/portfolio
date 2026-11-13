// [담당업무 1] Saga 상태 → 다음 Step/Process 이벤트 매핑 (정상 모드 · 보상 모드).

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// 이벤트 매핑 확장 메서드
    /// 
    /// 책임:
    /// - Saga State → Event 속성 매핑 간소화
    /// - 반복적인 속성 할당 코드 제거
    /// - 일관된 매핑 로직 제공
    /// </summary>
    public static class EventMappingExtensions
    {
        /// <summary>
        /// ChunkStepSagaState → ChunkStepDomainEvent 매핑
        /// </summary>
        public static void MapFromSagaState(
            this ChunkStepDomainEventBase evt,
            ChunkStepSagaState saga,
            ChunkStepPlanInfo step)
        {
            // Job scope
            evt.JobId = saga.JobId;
            evt.DomainType = saga.DomainType;
            evt.TotalChunkCount = saga.TotalChunkCount;
            evt.Delimiter = saga.Delimiter ?? string.Empty;
            evt.UserId = saga.UserId;

            // Chunk scope
            evt.ChunkId = saga.ChunkId;
            evt.Identifier = saga.Identifier;
            evt.ChunkIndex = saga.ChunkIndex;

            // Step scope
            evt.StepIndex = step.Index;
            evt.StepType = step.StepType;

            // Execution context
            evt.PayloadJson = saga.PayloadJson;
            evt.RequestBrandCode = saga.RequestBrandCode;
            evt.PartialFailureDetailsJson = saga.PartialFailureDetailsJson;

            // Default values (정상 실행 모드)
            evt.IsCompensation = false;
            evt.TargetIdentifiers = new List<string>();
            evt.CompensationId = null;
        }

        /// <summary>
        /// ChunkStepCompensationSagaState → ChunkStepDomainEvent 매핑 (보상 모드)
        /// </summary>
        public static void MapFromCompensationSagaState(
            this ChunkStepDomainEventBase evt,
            ChunkStepCompensationSagaState saga,
            CompensationStepInfo step,
            List<string> failedIdentifiers)
        {
            // Job scope
            evt.JobId = saga.JobId;
            evt.DomainType = saga.DomainType;
            evt.TotalChunkCount = saga.TotalChunkCount;
            evt.Delimiter = saga.Delimiter ?? string.Empty;
            evt.UserId = saga.UserId;

            // Chunk scope
            evt.ChunkId = saga.ChunkId;
            evt.Identifier = saga.Identifier;
            evt.ChunkIndex = saga.ChunkIndex;

            // Step scope
            evt.StepIndex = step.StepIndex;
            evt.StepType = step.StepType;

            // Execution context
            evt.PayloadJson = saga.PayloadJson;
            evt.RequestBrandCode = saga.RequestBrandCode;

            // Compensation mode
            evt.IsCompensation = true;
            evt.CompensationId = saga.CompensationId;
            evt.TargetIdentifiers = failedIdentifiers;
        }

        /// <summary>
        /// JobProcessSagaState → JobProcessDomainEvent 매핑
        /// </summary>
        public static void MapFromSagaState(
            this JobProcessDomainEventBase evt,
            JobProcessSagaState saga,
            ProcessPlanInfo process)
        {
            // Job scope
            evt.JobId = saga.CorrelationId;
            evt.JobIdentifier = saga.JobIdentifier;
            evt.DomainType = saga.DomainType;
            evt.TotalChunkCount = saga.TotalChunkCount;
            evt.Delimiter = saga.Delimiter ?? string.Empty;
            evt.UserId = saga.UserId;

            // Process scope
            evt.ProcessId = process.ProcessId;
            evt.ProcessIndex = process.Index;
            evt.ProcessType = process.ProcessType;

            // Execution context
            evt.JobStatus = saga.JobStatus;
            evt.RequestBrandCode = saga.RequestBrandCode;
            evt.SlipDiv = saga.SlipDiv;

            evt.PayloadJson = saga.PayloadJson;
        }

        /// <summary>
        /// ChunkStepSagaState → CompensationInitEvent 매핑
        /// </summary>
        public static void MapFromSagaStateForCompensation(
            this ChunkStepCompensationInitEvent evt,
            ChunkStepSagaState saga,
            Guid compensationId,
            string compensationPlanJson,
            List<string> failedIdentifiers)
        {
            // Job scope
            evt.JobId = saga.JobId;
            evt.DomainType = saga.DomainType;
            evt.TotalChunkCount = saga.TotalChunkCount;
            evt.Delimiter = saga.Delimiter ?? string.Empty;
            evt.UserId = saga.UserId;

            // Chunk scope
            evt.ChunkId = saga.ChunkId;
            evt.Identifier = saga.Identifier;
            evt.ChunkIndex = saga.ChunkIndex;

            // Compensation scope
            evt.CompensationId = compensationId;
            evt.CompensationPlanJson = compensationPlanJson;
            evt.FailedIdentifiers = failedIdentifiers;

            // Execution context
            evt.PayloadJson = saga.PayloadJson;
            evt.RequestBrandCode = saga.RequestBrandCode;
            evt.SlipDiv = saga.SlipDiv;
        }
    }
}
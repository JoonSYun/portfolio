// [담당업무 2] 신규 도메인 예시 — ChunkStepConsumerBase 를 상속해 비즈니스 호출과 멱등성 조회만 구현한다.

using MassTransit;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Saga;
using Portfolio.WmsOrchestration.Service;
using static Portfolio.WmsOrchestration.Model.Register;

namespace Portfolio.WmsOrchestration.Consumers
{
    public class WarehousesReqInsertConsumer
        : ChunkStepConsumerBase<WarehousesReqInsertEvent, WarehousesReqList_NT>
    {
        private readonly IMasterService<WarehousesReqList_NT> _warehousesReqService;
        private readonly IdempotencyService _idempotencyService;

        public WarehousesReqInsertConsumer(
            ILogger<WarehousesReqInsertConsumer> logger,
            IMasterService<WarehousesReqList_NT> warehousesReqService,
            IdempotencyService idempotencyService
        )
            : base(logger)
        {
            _warehousesReqService = warehousesReqService;
            _idempotencyService = idempotencyService;
        }

        protected override async Task<StepConsumerResponse> ProcessAsync(
            ConsumeContext<WarehousesReqInsertEvent> context)
        {
            RegisterResp resp = await _warehousesReqService.ProcessAsync(
                context.Message.JobId,
                RequestPayload.ToList(),
                "COM",
                "TA-01-02",
                "M",
                context.Message.ChunkIndex
            );

            return GetResult(resp);
        }

        protected override async Task<(IdempotencyResult, ChunkScopeStatus)> CheckIdempotencyAsync(
            ConsumeContext<WarehousesReqInsertEvent> context)
        {
            return await _idempotencyService.CheckIdempotency(
                RequestPayload.First()!.docNo,
                context.Message.ChunkIndex
            );
        }

        protected override Task CompensateAsync(
            ConsumeContext<WarehousesReqInsertEvent> context)
        {
            // 현재는 보상 로직 없음
            return Task.CompletedTask;
        }

        protected override Task<IdempotencyResult> CheckCompensationIdempotencyAsync(
            ConsumeContext<WarehousesReqInsertEvent> context)
        {
            return Task.FromResult(IdempotencyResult.NONE);
        }
    }
}

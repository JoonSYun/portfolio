// [담당업무 2] 신규 도메인 예시 — NTISConsumerBase 를 상속해 프로시저 호출 한 줄만 구현한다.

using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Service;
using System.Data;

namespace Portfolio.WmsOrchestration.Consumers
{
    /// <summary>
    /// 창고마스터 동기화 Consumer
    /// - 창고마스터_수신_TEXT
    /// - Procedure: USP_COM_WMS_M_WAREHOUSES_SET
    /// </summary>
    public class NTISWarehouseMasterSyncConsumer
        : NTISConsumerBase<NTISWarehouseMasterSyncEvent, NTISMasterParam>
    {
        public NTISWarehouseMasterSyncConsumer(
            ILogger<NTISWarehouseMasterSyncConsumer> logger,
            NTISService tisService,
            NTISResultService resultService,
            NTISSchedulerRedisManager redisManager)
            : base(logger, tisService, resultService, redisManager)
        {
        }

        protected override Task<DataTable> ExecuteProcedureAsync(NTISMasterParam payload)
        {
            return TisService.ExecuteAsync(payload, "USP_COM_WMS_M_WAREHOUSES_SET");
        }
    }
}
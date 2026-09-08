using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using System.Data;

namespace Portfolio.WmsOrchestration.Service
{
    /// <summary>
    /// NTISService
    /// -----------
    /// TIS 프로시저 실행 서비스
    ///
    /// 책임:
    ///   ✔ NIFService를 통한 프로시저 실행
    ///   ✔ 프로시저명은 Consumer에서 직접 전달받음
    ///
    /// 변경 사항:
    ///   - URI → Procedure 매핑 제거
    ///   - 프로시저명을 파라미터로 직접 받음
    ///   - 각 도메인별 Execute 메서드는 유지 (타입 안정성)
    /// </summary>
    public class NTISService
    {
        private readonly NIFService _nifService;
        private readonly ILogger<NTISService> _logger;

        public NTISService(
            NIFService nifService,
            ILogger<NTISService> logger)
        {
            _nifService = nifService;
            _logger = logger;
        }

        #region Master Sync

        public async Task<DataTable> ExecuteAsync(
            object param,
            string procedureName)
        {
            _logger.LogInformation(
                AppLog.Log("[NTISService] Executing NTIS Procedure. Procedure={Procedure}"),
                procedureName);

            return await _nifService.ExecuteIFProcedureAsync(procedureName, param);
        }

        #endregion
    }
}
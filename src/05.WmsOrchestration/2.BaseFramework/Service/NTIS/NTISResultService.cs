using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Service
{
    /// <summary>
    /// NTISResultService
    /// -----------------
    /// TIS 스케줄러 실행 결과 저장 서비스
    ///
    /// 책임:
    ///   ✔ 실행 결과를 DB에 저장
    ///   ✔ 운영 추적성 제공
    ///   ✔ UI 리포팅 지원
    ///
    /// 중요:
    ///   - 저장 실패는 비즈니스 플로우를 중단시키지 않음
    ///   - 에러를 삼키고 로그만 남김
    /// </summary>
    public class NTISResultService
    {
        private readonly DBProcedureService _dbService;
        private readonly ILogger<NTISResultService> _logger;

        public NTISResultService(
            DBProcedureService dbService,
            ILogger<NTISResultService> logger)
        {
            _dbService = dbService;
            _logger = logger;
        }

        /// <summary>
        /// 스케줄러 실행 결과 저장
        /// Procedure: USP_TEST_INSERTRESULT_SET
        /// </summary>
        public async Task<bool> SaveExecutionResultAsync(
            NTISTargetDto targetInfo,
            string status,
            string errorMessage = null)
        {
            try
            {
                var param = new
                {
                    BCODE = targetInfo.BCODE,
                    HOSTSYSTEM = targetInfo.HOST_SYSTEM,
                    APICODE = targetInfo.API_CODE,
                    APINAME = targetInfo.API_NAME,
                    RESULT = status,
                    REGDATE = DateTime.Now,
                    REGUSER = "Scheduler",
                    METHODNAME = targetInfo.URI,
                    ERRMSG = errorMessage ?? ""
                };

                await _dbService.ExecuteNonQueryAsync(
                    "USP_TEST_INSERTRESULT_SET",
                    param);

                _logger.LogDebug(
                    AppLog.Log("[NTISResultService] Result saved. {Identifier}, Result={Result}"),
                    targetInfo.GetLogIdentifier(),
                    status);

                return true;
            }
            catch (Exception ex)
            {
                // 결과 저장 실패는 비즈니스 플로우를 중단시키지 않음
                _logger.LogError(
                    ex,
                    AppLog.Log("[NTISResultService] Failed to save result. {Identifier}"),
                    targetInfo.GetLogIdentifier());

                return false;
            }
        }
    }
}
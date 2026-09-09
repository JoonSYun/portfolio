using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using System.Data;

namespace Portfolio.WmsOrchestration.Service
{
    /// <summary>
    /// NIFService
    /// ----------
    /// TIS(WMS Interface System) 표준 트랜잭션 처리 서비스
    ///
    /// 책임:
    ///   ✔ TIS 프로시저 트랜잭션 실행
    ///   ✔ 결과 검증 및 에러 판단
    ///   ✔ 표준 결과 DataTable 생성
    ///
    /// 중요:
    ///   - TIS 프로시저는 예외 대신 결과 테이블로 에러를 반환함
    ///   - 트랜잭션은 명시적으로 관리됨 (Begin → Commit/Rollback)
    /// </summary>
    public class NIFService
    {
        private readonly DBProcedureService _dbService;
        private readonly ILogger<NIFService> _logger;

        public NIFService(
            DBProcedureService dbService,
            ILogger<NIFService> logger)
        {
            _dbService = dbService;
            _logger = logger;
        }

        #region ================= TIS Transaction Execution =================

        /// <summary>
        /// TIS 표준 트랜잭션 패턴으로 프로시저를 실행합니다.
        ///
        /// 동작:
        ///   - 명시적 트랜잭션 시작
        ///   - 성공 시 커밋
        ///   - 에러 결과 또는 예외 시 롤백
        ///
        /// 주의:
        ///   TIS 프로시저는 SQL 에러를 발생시키지 않고 결과 테이블로 반환하므로
        ///   실행 후 논리적 검증이 필요합니다.
        /// </summary>
        public async Task<DataTable> ExecuteIFProcedureAsync(
            string procedureName,
            object parameters = null)
        {
            DataTable dataTable = null;

            if (parameters != null && parameters is NTISParamBase paramBase)
            {
                try
                {
                    await _dbService.BeginTransactionAsync();

                    _logger.LogInformation(
                        AppLog.Log("[NIFService] Execute TIS: {Procedure}"),
                        procedureName);

                    dataTable = await _dbService.ExecuteAsync(procedureName, parameters);

                    if (dataTable.Rows.Count == 0)
                    {
                        _dbService.Commit();
                        return CreateSuccessDataTable();
                    }

                    bool hasError = CheckIFError(dataTable);

                    if (hasError)
                    {
                        _logger.LogError(
                            AppLog.Log("[NIFService] TIS Procedure Error Detected: Procedure={Procedure}, Identifier={Identifier}"),
                            procedureName, paramBase.TargetInfo.GetLogIdentifier());
                        _dbService.Rollback();
                    }
                    else
                        _dbService.Commit();

                    return dataTable;
                }
                catch (Exception ex)
                {
                    _dbService.Rollback();

                    throw new Exception(
                        $"Procedure: {procedureName}, Error: {ex.Message}", ex);
                }
            }
            else
            {
                try
                {
                    await _dbService.BeginTransactionAsync();

                    dataTable = await _dbService.ExecuteAsync(procedureName, parameters);

                    if (dataTable.Rows.Count == 0)
                    {
                        _dbService.Commit();
                        return CreateSuccessDataTable();
                    }

                    bool hasError = CheckIFError(dataTable);

                    if (hasError)
                    {
                        _logger.LogError(
                            AppLog.Log("[NIFService] TIS Procedure Error Detected: Procedure={Procedure}"),
                            procedureName);
                        _dbService.Rollback();
                    }
                    else
                        _dbService.Commit();

                    return dataTable;
                }
                catch (Exception ex)
                {
                    _dbService.Rollback();

                    _logger.LogError(
                        AppLog.Log("[NIFService] Transaction Begin Error: {Procedure}, Error: {Error}"),
                        procedureName, ex.Message);

                    throw new Exception(
                        $"Procedure: {procedureName}, Error: {ex.Message}", ex);
                }
            }
        }

        #endregion

        #region ================= Error Interpretation =================

        /// <summary>
        /// 반환된 DataTable을 평가하여 TIS가 실패를 신호했는지 판단합니다.
        ///
        /// 규칙:
        ///   - 첫 번째 컬럼에 "0.xxx" 또는 "9.xxx" 형식의 코드 포함 시 실패
        ///   - 해당 형식의 행이 존재하면 논리적 실패
        ///
        /// 예외 처리:
        ///   - 예상치 못한 구조는 false 반환 (성공으로 간주)
        /// </summary>
        private bool CheckIFError(DataTable dt)
        {
            // 성공
            if (dt == null || dt.Rows.Count == 0 || dt.Columns.Count == 0)
                return false;

            try
            {
                // 0 또는 9로 시작하는 데이터가 있으면 실패(SYSTEM ERROR)
                // 나머지의 경우엔 COMMIT은 해줌
                string name = dt.Columns[0].ColumnName;
                var rows = dt.Select($"{name} LIKE '0.%' OR {name} LIKE '9.%'");
                return rows.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region ================= DataTable Helpers =================

        /// <summary>
        /// TIS 프로시저가 사용하는 표준 성공 결과 형식입니다.
        /// </summary>
        public static DataTable CreateSuccessDataTable(string msg = "")
        {
            var dt = new DataTable("WebService_Return_MSG");
            dt.Columns.Add("처리결과");
            dt.Columns.Add("메시지");
            dt.Columns.Add("추가메시지");

            var row = dt.NewRow();
            row["처리결과"] = "성공";
            row["메시지"] = "정상 처리 되었습니다.";
            row["추가메시지"] = msg;
            dt.Rows.Add(row);

            return dt;
        }

        /// <summary>
        /// 애플리케이션 레벨 실패를 위한 표준 에러 결과 테이블입니다.
        /// </summary>
        public static DataTable CreateErrorDataTable(string msg)
        {
            var dt = new DataTable("WebService_Return_MSG");
            dt.Columns.Add("범위");
            dt.Columns.Add("항목");
            dt.Columns.Add("메시지");

            var row = dt.NewRow();
            row["범위"] = "0.전체";
            row["항목"] = "실패";
            row["메시지"] = msg;
            dt.Rows.Add(row);

            return dt;
        }

        /// <summary>
        /// TIS 반환 테이블이 성공을 나타내는지 평가합니다.
        /// </summary>
        public static bool IsSuccessResult(DataTable dt)
        {
            if (dt == null || dt.Rows.Count == 0)
                return false;

            if (dt.TableName == "WebService_Return_MSG" && dt.Rows[0]["처리결과"].ToString() == "성공") return true;
            else return false;
        }

        #endregion
    }
}
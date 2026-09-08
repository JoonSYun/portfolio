using System.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Model;
using System.Data;
using System.Data.Common;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Stored Procedure 실행 서비스 (ADO.NET 기반)
    /// 
    /// 주요 기능:
    /// - Scoped 생명주기로 관리되며, 트랜잭션 지원
    /// - DataTable 파라미터 직접 지원 (TVP - Table-Valued Parameters)
    /// - 복잡한 객체 타입 자동 필터링 (SQL 호환 타입만 파라미터로 변환)
    /// - 순차/병렬 실행 모드 지원 (useNewConnection 플래그)
    /// 
    /// 연결 관리 전략:
    /// - 트랜잭션 활성화 시: 트랜잭션 전용 연결 사용
    /// - 순차 실행 시 (기본): Scope 단위 연결 재사용
    /// - 병렬 실행 시 (useNewConnection=true): 매번 새 연결 생성
    /// 
    /// 기존 레거시 시스템:
    /// - TlkTranscope + HelperClass 역할 대체
    /// </summary>
    public class DBProcedureService : IDisposable
    {
        private readonly IDBConnectionFactory _connectionFactory;
        private readonly ILogger<DBProcedureService> _logger;

        // 트랜잭션 전용 연결 (트랜잭션 활성화 시에만 사용)
        private IDbConnection _transactionConnection;
        private IDbTransaction _transaction;

        // Scope 단위 연결 (일반 순차 실행용)
        private IDbConnection _scopedConnection;

        private bool _disposed = false;

        public DBProcedureService(
            IDBConnectionFactory connectionFactory,
            ILogger<DBProcedureService> logger)
        {
            _connectionFactory = connectionFactory;
            _logger = logger;
        }

        #region Connection Management

        /// <summary>
        /// 데이터베이스 연결 획득
        /// - 트랜잭션 활성화 시: 트랜잭션 전용 연결 반환 (항상 재사용)
        /// - 트랜잭션 비활성 시:
        ///   - useNewConnection = false (기본값): Scope 단위 연결 재사용
        ///   - useNewConnection = true: 매번 새 연결 생성 (병렬 처리용)
        /// </summary>
        private async Task<IDbConnection> GetConnectionAsync(
            DatabaseType dbType = DatabaseType.MSSQL,
            bool useNewConnection = false)
        {
            // 트랜잭션이 활성화되어 있으면 트랜잭션 전용 연결 반환
            if (_transaction != null)
            {
                return _transactionConnection;
            }

            // 새 연결이 필요한 경우 (병렬 처리용)
            if (useNewConnection)
            {
                var newConnection = _connectionFactory.CreateConnection(dbType);
                await Task.Run(() => newConnection.Open());
                return newConnection;
            }

            // Scope 단위 연결 재사용 (순차 처리용)
            if (_scopedConnection == null)
            {
                _scopedConnection = _connectionFactory.CreateConnection(dbType);
                await Task.Run(() => _scopedConnection.Open());
            }

            return _scopedConnection;
        }

        /// <summary>
        /// 트랜잭션 시작
        /// - 이미 트랜잭션이 있으면 예외 발생
        /// - 트랜잭션 전용 연결 생성
        /// </summary>
        public async Task BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            DatabaseType dbType = DatabaseType.MSSQL)
        {
            if (_transaction != null)
            {
                throw new InvalidOperationException("Transaction already started");
            }

            _transactionConnection = _connectionFactory.CreateConnection(dbType);
            await Task.Run(() => _transactionConnection.Open());
            _transaction = _transactionConnection.BeginTransaction(isolationLevel);
        }

        /// <summary>
        /// 트랜잭션 커밋
        /// - 트랜잭션이 없으면 예외 발생
        /// </summary>
        public void Commit()
        {
            if (_transaction == null)
            {
                throw new InvalidOperationException("No active transaction to commit");
            }

            _transaction.Commit();
            _transaction.Dispose();
            _transaction = null;
        }

        /// <summary>
        /// 트랜잭션 롤백
        /// - 트랜잭션이 없으면 경고 로그만 남기고 무시
        /// </summary>
        public void Rollback()
        {
            if (_transaction == null)
            {
                _logger.LogWarning(AppLog.Log("[DBProcedureService] No active transaction to rollback"));
                return;
            }

            _transaction.Rollback();
            _transaction.Dispose();
            _transaction = null;
        }

        #endregion

        #region Query Methods (SELECT)

        /// <summary>
        /// Stored Procedure 실행 - DataTable 반환 (조회용)
        /// 
        /// 사용 시나리오:
        /// - SELECT 결과를 DataTable로 받아야 하는 경우
        /// - 트랜잭션 내에서 조회가 필요한 경우
        /// 
        /// 연결 관리:
        /// - useNewConnection = false (기본값): Scope 단위 연결 재사용 (순차 처리용)
        ///   예) foreach 루프에서 여러 번 호출 시 같은 연결 재사용
        /// - useNewConnection = true: 매번 새 연결 생성 (병렬 처리용)
        ///   예) Task.WhenAll로 동시에 여러 쿼리 실행 시
        /// </summary>
        /// <param name="procedureName">실행할 프로시저명</param>
        /// <param name="parameters">익명 객체, Dictionary, 또는 클래스 (복잡한 객체는 자동 제외)</param>
        /// <param name="commandTimeout">타임아웃 (초 단위, 기본 30분)</param>
        /// <param name="dbType">데이터베이스 타입 (기본 MSSQL)</param>
        /// <param name="useNewConnection">매번 새 연결 생성 여부 (병렬 처리 시 true)</param>
        public async Task<DataTable> ExecuteQueryAsync(
            string procedureName,
            object parameters = null,
            int commandTimeout = 1800,
            DatabaseType dbType = DatabaseType.MSSQL,
            bool useNewConnection = false)
        {
            var conn = await GetConnectionAsync(dbType, useNewConnection);

            // useNewConnection = true 이고 트랜잭션이 없을 때만 연결 해제 필요
            var shouldDispose = useNewConnection && _transaction == null;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = procedureName;
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = commandTimeout;
                cmd.Transaction = _transaction;

                // 파라미터 추가 (SQL 호환 타입만 자동 필터링)
                AddParameters(cmd, parameters);

                var dataTable = new DataTable(procedureName);
                dataTable.Clear();

                // DataAdapter 사용 (트랜잭션 상태와 무관하게 결과 읽기 가능)
                if (conn is SqlConnection sqlConn)
                {
                    using var adapter = new SqlDataAdapter((SqlCommand)cmd);
                    await Task.Run(() => adapter.Fill(dataTable));
                }
                else
                {
                    var adapter = CreateDataAdapter(conn, cmd);
                    adapter.Fill(dataTable);
                }

                return dataTable;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[DBProcedureService] Query failed. Procedure={ProcedureName}"),
                    procedureName);

                throw new Exception($"프로시저명: {procedureName}, 에러 내용: {ex.Message}", ex);
            }
            finally
            {
                // 새 연결을 생성한 경우에만 해제
                if (shouldDispose)
                {
                    conn.Close();
                    conn.Dispose();
                }
            }
        }

        /// <summary>
        /// Stored Procedure 실행 - 강타입 리스트 반환
        /// 
        /// 사용 시나리오:
        /// - SELECT 결과를 List&lt;T&gt;로 받고 싶은 경우
        /// - DataTable → 강타입 변환 자동 처리
        /// </summary>
        /// <param name="useNewConnection">매번 새 연결 생성 여부 (병렬 처리 시 true)</param>
        public async Task<List<T>> ExecuteQueryAsync<T>(
            string procedureName,
            object parameters = null,
            int commandTimeout = 1800,
            DatabaseType dbType = DatabaseType.MSSQL,
            bool useNewConnection = false) where T : new()
        {
            var dataTable = await ExecuteQueryAsync(procedureName, parameters, commandTimeout, dbType, useNewConnection);
            return ConvertDataTableToList<T>(dataTable);
        }

        #endregion

        #region Execute Methods (INSERT/UPDATE/DELETE)

        /// <summary>
        /// Stored Procedure 실행 - DataTable 반환 (변경 작업용)
        /// 
        /// 사용 시나리오:
        /// - INSERT/UPDATE/DELETE 후 결과를 DataTable로 받아야 하는 경우
        /// - 프로시저가 SELECT 결과를 반환하는 경우
        /// 
        /// 주의사항:
        /// - 레거시 TlkTranscope 방식과 호환
        /// - DataAdapter.Fill() 사용으로 트랜잭션 내에서도 결과 읽기 가능
        /// - 변경 작업은 기본적으로 순차 실행 권장
        /// - useNewConnection은 특수한 경우에만 사용
        /// </summary>
        public async Task<DataTable> ExecuteAsync(
            string procedureName,
            object parameters = null,
            int commandTimeout = 1800,
            DatabaseType dbType = DatabaseType.MSSQL,
            bool useNewConnection = false)
        {
            var conn = await GetConnectionAsync(dbType, useNewConnection);
            var shouldDispose = useNewConnection && _transaction == null;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = procedureName;
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = commandTimeout;
                cmd.Transaction = _transaction;

                // 파라미터 추가 (SQL 호환 타입만 자동 필터링)
                AddParameters(cmd, parameters);

                var dataTable = new DataTable(procedureName);
                dataTable.Clear();

                // DataAdapter 사용 (레거시 TlkTranscope 방식)
                if (conn is SqlConnection sqlConn)
                {
                    using var adapter = new SqlDataAdapter((SqlCommand)cmd);
                    await Task.Run(() => adapter.Fill(dataTable));
                }
                else
                {
                    var adapter = CreateDataAdapter(conn, cmd);
                    adapter.Fill(dataTable);
                }

                return dataTable;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[DBProcedureService] Procedure execution failed. Procedure={ProcedureName}"),
                    procedureName);

                throw new Exception($"프로시저명: {procedureName}, 에러 내용: {ex.Message}", ex);
            }
            finally
            {
                if (shouldDispose)
                {
                    conn.Close();
                    conn.Dispose();
                }
            }
        }

        /// <summary>
        /// Stored Procedure 실행 - NonQuery (영향받은 행 수만 반환)
        /// 
        /// 사용 시나리오:
        /// - INSERT/UPDATE/DELETE만 수행하고 결과가 필요없는 경우
        /// - 반환값이 행 개수만 필요한 경우
        /// </summary>
        public async Task<int> ExecuteNonQueryAsync(
            string procedureName,
            object parameters = null,
            int commandTimeout = 1800,
            DatabaseType dbType = DatabaseType.MSSQL,
            bool useNewConnection = false)
        {
            var conn = await GetConnectionAsync(dbType, useNewConnection);
            var shouldDispose = useNewConnection && _transaction == null;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = procedureName;
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = commandTimeout;
                cmd.Transaction = _transaction;

                // 파라미터 추가 (SQL 호환 타입만 자동 필터링)
                AddParameters(cmd, parameters);

                return await ExecuteNonQueryAsync(cmd);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[DBProcedureService] Non-query execution failed. Procedure={ProcedureName}"),
                    procedureName);

                throw new Exception($"프로시저명: {procedureName}, 에러 내용: {ex.Message}", ex);
            }
            finally
            {
                if (shouldDispose)
                {
                    conn.Close();
                    conn.Dispose();
                }
            }
        }

        #endregion

        #region Parameter Handling

        /// <summary>
        /// 파라미터 추가 (익명 객체, Dictionary, 클래스 지원)
        /// 
        /// 지원 형식:
        /// 1. 익명 객체: new { BCODE = "03", NAME = "테스트" }
        /// 2. Dictionary: new Dictionary&lt;string, object&gt; { ["BCODE"] = "03" }
        /// 3. 클래스: new MyParam { BCODE = "03", TargetInfo = targetDto }
        /// 
        /// 자동 필터링:
        /// - SQL 호환 타입만 파라미터로 추가
        /// - 복잡한 객체(클래스, 구조체)는 자동으로 제외
        /// - DataTable은 TVP(Table-Valued Parameters)로 자동 변환
        /// </summary>
        private void AddParameters(IDbCommand cmd, object parameters)
        {
            if (parameters == null)
                return;

            // Dictionary<string, object> 처리
            if (parameters is Dictionary<string, object> dict)
            {
                foreach (var kvp in dict)
                {
                    AddParameter(cmd, kvp.Key, kvp.Value);
                }
                return;
            }

            // 익명 객체 또는 클래스 처리
            var properties = parameters.GetType().GetProperties();
            foreach (var prop in properties)
            {
                var value = prop.GetValue(parameters);

                // SQL 파라미터로 변환 가능한 타입만 추가
                if (IsSqlCompatibleType(prop.PropertyType, value))
                {
                    AddParameter(cmd, $"@{prop.Name}", value);
                }
                // 복잡한 타입은 자동으로 건너뜀 (로그 없음)
            }
        }

        /// <summary>
        /// SQL 파라미터로 변환 가능한 타입인지 확인
        /// 
        /// Type.GetTypeCode() 기반 - MS 표준 방식 사용
        /// 
        /// 허용되는 타입:
        /// - 기본 타입: int, long, bool, byte, short, float, double 등
        /// - Enum
        /// - string, DateTime, decimal
        /// - Guid, byte[], TimeSpan, DateTimeOffset
        /// - DataTable (TVP로 변환)
        /// - Nullable&lt;T&gt; (T가 위 타입인 경우)
        /// 
        /// 제외되는 타입:
        /// - 사용자 정의 클래스
        /// - 사용자 정의 구조체
        /// - 그 외 복잡한 객체
        /// </summary>
        private bool IsSqlCompatibleType(Type type, object value)
        {
            // null은 허용 (DBNull로 변환)
            if (value == null)
                return true;

            // DataTable은 TVP(Table-Valued Parameters)로 처리 가능
            if (value is DataTable)
                return true;

            // Nullable<T> 타입은 내부 타입으로 검사
            var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

            // 기본 타입 체크 (int, long, bool, byte, short, float, double 등)
            if (underlyingType.IsPrimitive || underlyingType.IsEnum)
                return true;

            // TypeCode로 추가 타입 체크
            var typeCode = Type.GetTypeCode(underlyingType);

            switch (typeCode)
            {
                case TypeCode.String:    // string
                case TypeCode.DateTime:  // DateTime
                case TypeCode.Decimal:   // decimal
                    return true;

                case TypeCode.Object:
                    // CLR 타입 중 SQL 호환 타입만 허용
                    return underlyingType == typeof(Guid) ||
                           underlyingType == typeof(byte[]) ||
                           underlyingType == typeof(TimeSpan) ||
                           underlyingType == typeof(DateTimeOffset);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 단일 파라미터 추가
        /// 
        /// DataTable 처리:
        /// - SqlCommand인 경우 TVP(Table-Valued Parameters)로 자동 변환
        /// - TableName을 기반으로 TVP 타입명 결정 (예: "UTY_COM_KEYSET" → "dbo.UTY_COM_KEYSET")
        /// 
        /// 일반 타입 처리:
        /// - IDbCommand.CreateParameter()로 생성
        /// - null은 DBNull.Value로 자동 변환
        /// </summary>
        private void AddParameter(IDbCommand cmd, string parameterName, object value)
        {
            // SqlCommand인 경우 DataTable → TVP 처리
            if (cmd is SqlCommand sqlCmd && value is DataTable dt)
            {
                var param = new SqlParameter
                {
                    ParameterName = parameterName,
                    SqlDbType = SqlDbType.Structured,
                    TypeName = GetTableTypeName(dt), // TVP 타입명
                    Value = dt
                };
                sqlCmd.Parameters.Add(param);
            }
            else
            {
                // 일반 파라미터
                var param = cmd.CreateParameter();
                param.ParameterName = parameterName;
                param.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(param);
            }
        }

        /// <summary>
        /// DataTable의 TVP 타입명 결정
        /// 
        /// 규칙:
        /// 1. DataTable.TableName이 있으면: "dbo.{TableName}" 사용
        ///    예: TableName = "UTY_COM_KEYSET" → "dbo.UTY_COM_KEYSET"
        /// 
        /// 2. TableName이 없으면: 기본값 사용 (현재는 에러 발생 가능)
        /// 
        /// 주의사항:
        /// - SQL Server에 동일한 이름의 TVP 타입이 정의되어 있어야 함
        /// - 프로시저 파라미터 타입과 일치해야 함
        /// </summary>
        private string GetTableTypeName(DataTable dt)
        {
            if (!string.IsNullOrEmpty(dt.TableName))
            {
                return $"dbo.{dt.TableName}";
            }

            // 기본 타입명 (프로젝트에 맞게 수정 필요)
            return "dbo.DEFAULT_TABLE_TYPE";
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// DB 타입에 맞는 DataAdapter 생성
        /// 
        /// 지원 DB:
        /// - SQL Server: SqlDataAdapter
        /// - PostgreSQL: 미구현 (필요시 추가)
        /// </summary>
        private DbDataAdapter CreateDataAdapter(IDbConnection conn, IDbCommand cmd)
        {
            switch (conn.GetType().Name)
            {
                case "SqlConnection":
                    return new SqlDataAdapter((SqlCommand)cmd);

                case "NpgsqlConnection":
                    throw new NotSupportedException("PostgreSQL DataAdapter not implemented yet");

                default:
                    throw new NotSupportedException($"Unsupported connection type: {conn.GetType().Name}");
            }
        }

        /// <summary>
        /// 비동기 ExecuteReader
        /// - SqlCommand는 네이티브 비동기 사용
        /// - 다른 DB는 Task.Run으로 동기 메서드 래핑
        /// </summary>
        private async Task<IDataReader> ExecuteReaderAsync(IDbCommand cmd)
        {
            if (cmd is SqlCommand sqlCmd)
            {
                return await sqlCmd.ExecuteReaderAsync();
            }

            return await Task.Run(() => cmd.ExecuteReader());
        }

        /// <summary>
        /// 비동기 ExecuteNonQuery
        /// - SqlCommand는 네이티브 비동기 사용
        /// - 다른 DB는 Task.Run으로 동기 메서드 래핑
        /// </summary>
        private async Task<int> ExecuteNonQueryAsync(IDbCommand cmd)
        {
            if (cmd is SqlCommand sqlCmd)
            {
                return await sqlCmd.ExecuteNonQueryAsync();
            }

            return await Task.Run(() => cmd.ExecuteNonQuery());
        }

        /// <summary>
        /// DataTable을 List&lt;T&gt;로 변환
        /// 
        /// 변환 규칙:
        /// - 프로퍼티명과 컬럼명이 일치하는 경우만 매핑
        /// - DBNull은 자동으로 건너뜀
        /// - 타입 불일치 시 예외 발생 가능
        /// </summary>
        private List<T> ConvertDataTableToList<T>(DataTable dt) where T : new()
        {
            var list = new List<T>();
            var properties = typeof(T).GetProperties();

            foreach (DataRow row in dt.Rows)
            {
                var item = new T();
                foreach (var prop in properties)
                {
                    if (dt.Columns.Contains(prop.Name) && row[prop.Name] != DBNull.Value)
                    {
                        prop.SetValue(item, row[prop.Name]);
                    }
                }
                list.Add(item);
            }

            return list;
        }

        /// <summary>
        /// DataTable을 로그용 문자열로 변환
        /// 
        /// 형식:
        /// - 첫 줄: 컬럼명 (탭 구분)
        /// - 이후: 각 행의 데이터 (탭 구분)
        /// - 최대 100행까지만 출력
        /// 
        /// 사용처:
        /// - 프로시저 실행 결과 로깅
        /// - 디버깅 용도
        /// </summary>
        public static string DataTableToString(DataTable dataTable)
        {
            if (dataTable == null || dataTable.Rows.Count == 0)
                return "[Empty DataTable]";

            var sb = new System.Text.StringBuilder();

            try
            {
                // 열 이름 출력
                foreach (DataColumn column in dataTable.Columns)
                {
                    sb.Append(column.ColumnName).Append("\t");
                }
                sb.AppendLine();

                // 각 행의 데이터 출력 (최대 100행까지만)
                int rowCount = Math.Min(dataTable.Rows.Count, 100);
                for (int i = 0; i < rowCount; i++)
                {
                    var row = dataTable.Rows[i];
                    foreach (var item in row.ItemArray)
                    {
                        sb.Append(item?.ToString() ?? "NULL").Append("\t");
                    }
                    sb.AppendLine();
                }

                if (dataTable.Rows.Count > 100)
                {
                    sb.AppendLine($"... and {dataTable.Rows.Count - 100} more rows");
                }
            }
            catch (Exception ex)
            {
                sb.Append($"DataTableToString error: {ex.Message}");
            }

            return sb.ToString();
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 리소스 해제
        /// - GC에게 Finalizer 호출 불필요함을 알림
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 실제 리소스 해제 로직
        /// 
        /// 처리 순서:
        /// 1. 활성 트랜잭션이 있으면 자동 롤백
        /// 2. 트랜잭션 전용 연결 해제
        /// 3. Scope 단위 연결 해제
        /// 
        /// 중요:
        /// - Scoped 생명주기이므로 요청 종료 시 자동 호출됨
        /// - 트랜잭션을 명시적으로 Commit하지 않으면 자동 롤백
        /// - useNewConnection=true로 생성한 연결들은 이미 각 메서드에서 해제됨
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // 트랜잭션이 아직 살아있으면 롤백
                if (_transaction != null)
                {
                    _logger.LogWarning(
                        AppLog.Log("[DBProcedureService] Disposing with active transaction. Rolling back."));

                    Rollback();
                }

                // 트랜잭션 전용 연결 해제
                if (_transactionConnection != null)
                {
                    if (_transactionConnection.State != ConnectionState.Closed)
                    {
                        _transactionConnection.Close();
                    }
                    _transactionConnection.Dispose();
                    _transactionConnection = null;
                }

                // Scope 단위 연결 해제
                if (_scopedConnection != null)
                {
                    if (_scopedConnection.State != ConnectionState.Closed)
                    {
                        _scopedConnection.Close();
                    }
                    _scopedConnection.Dispose();
                    _scopedConnection = null;
                }
            }

            _disposed = true;
        }

        #endregion
    }
}
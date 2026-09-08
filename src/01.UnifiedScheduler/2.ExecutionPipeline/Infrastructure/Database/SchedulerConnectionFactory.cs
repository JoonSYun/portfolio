using Microsoft.Data.SqlClient;

namespace Portfolio.UnifiedScheduler.Infrastructure.Database
{
    public class SchedulerConnectionFactory
    {
        private readonly IConfiguration _config;
        public SchedulerConnectionFactory(IConfiguration config)
        {
            _config = config;
        }

        public async Task<ISchedulerConnection> CreateAsync(string dbName)
        {
            string connStr = _config.GetConnectionString(dbName)
                ?? throw new InvalidOperationException($"Connection string '{dbName}' not found");
            return await OpenAsync(connStr);
        }

        /// <summary>
        /// 설정 조회 없이 주어진 접속 문자열로 직접 접속한다.
        /// 페이로드에서 접속 문자열을 지정한 경우(<c>TARGET_CONNECTION_STRING</c>)에 사용한다.
        /// </summary>
        public async Task<ISchedulerConnection> CreateFromConnectionStringAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be empty", nameof(connectionString));
            return await OpenAsync(connectionString);
        }

        private static async Task<ISchedulerConnection> OpenAsync(string connStr)
        {
            SqlConnection conn = new SqlConnection(connStr);

            try
            {
                await conn.OpenAsync();
                return new SchedulerConnection(conn);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }
    }
}

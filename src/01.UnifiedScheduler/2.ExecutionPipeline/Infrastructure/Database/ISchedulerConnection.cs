using System.Data;

namespace Portfolio.UnifiedScheduler.Infrastructure.Database
{
    public interface ISchedulerConnection : IDisposable
    {
        public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CommandType commandType = CommandType.StoredProcedure, int? commandTimeout = null, CancellationToken cancellationToken = default);
        public Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CommandType commandType = CommandType.StoredProcedure, int? commandTimeout = null, CancellationToken cancellationToken = default);
        public Task<int> ExecuteAsync(string sql, object? param = null, CommandType commandType = CommandType.StoredProcedure, int? commandTimeout = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// 프로시저 시그니처가 런타임에만 정해지는(파라미터 이름/개수가 고정 안 된) 호출용 오버로드.
        /// key/value 딕셔너리를 <see cref="Dapper.DynamicParameters"/> 로 바인딩해 실행한다 —
        /// 컴파일 타임 익명 객체를 만들 수 없는 케이스(예: <c>DBProcedureExecuteJob</c>)를 위한 것이다.
        /// <paramref name="parameters"/> 가 null/빈 딕셔너리면 무인자 프로시저로 호출한다.
        /// </summary>
        public Task<int> ExecuteAsync(string sql, IDictionary<string, string>? parameters, CommandType commandType = CommandType.StoredProcedure, int? commandTimeout = null, CancellationToken cancellationToken = default);
    }
}

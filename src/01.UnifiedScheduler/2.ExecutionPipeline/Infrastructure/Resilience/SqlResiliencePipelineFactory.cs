using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Retry;

namespace Portfolio.UnifiedScheduler.Infrastructure.Resilience;

/// <summary>
/// SQL Server transient 오류 자동 재시도용 <see cref="ResiliencePipeline"/> 팩토리. SCH_JOB_LOG 의 상태 전이
/// 같은 idempotent UPDATE 호출에 1회 감싸 deadlock(1205) · command timeout(-2) ·
/// connection broken(4060) · login throttling(40197 / 40501 / 40613 / 49918 / 49919 / 49920) 케이스에서
/// 짧은 exponential backoff 재시도를 제공한다.
///
/// <para>
/// EF Core 의 <c>EnableRetryOnFailure</c> (SaveChanges 단위) 와 다르게 <c>ExecuteUpdate</c> / <c>SaveChanges</c>
/// 어느 호출에도 한 줄로 적용 가능 — Repository 가 직접 <c>Pipeline.Execute(...)</c> 로 감싼다.
/// idempotent 한 status 전이 + LogId 식별 UPDATE 만 적용하므로 재시도 안전.
/// </para>
///
/// <para>
/// Singleton 으로 등록 — pipeline 자체가 thread-safe + 무상태이므로 호출처 모두에서 공유.
/// </para>
/// </summary>
public sealed class SqlResiliencePipelineFactory
{
    /// <summary>
    /// Azure SQL / SQL Server 가 transient 로 분류하는 대표 오류 번호 — Microsoft.EntityFrameworkCore.SqlServer 의
    /// <c>SqlServerTransientExceptionDetector</c> 와 동일 셋. 운영 incident 로그에서 확인된 케이스만 추가하고
    /// permanent 오류(syntax / FK / UNIQUE 등) 는 의도적으로 제외 — 재시도해도 같은 결과이므로 의미 없는 지연만 늘어남.
    /// </summary>
    private static readonly int[] TransientSqlErrorNumbers =
    {
        1205,    // deadlock victim
        -2,      // command timeout
        4060,    // cannot open database
        40197,   // the service has encountered an error processing your request
        40501,   // service is currently busy
        40613,   // database is currently unavailable
        49918,   // not enough resources to process request
        49919,   // too many create/update operations in progress
        49920,   // too many operations in progress for subscription
    };

    private readonly ResiliencePipeline _pipeline;

    public SqlResiliencePipelineFactory()
    {
        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                // PredicateBuilder.Handle<T>(predicate) 체인 — DbUpdateException 의 inner SqlException 과
                // raw SqlException 둘 다 transient 번호인지 검사한다.
                ShouldHandle = new PredicateBuilder()
                    .Handle<DbUpdateException>(IsTransient)
                    .Handle<SqlException>(IsTransient),
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                Delay            = TimeSpan.FromMilliseconds(150),
                UseJitter        = true,
            })
            .Build();
    }

    /// <summary>SCH_JOB_LOG 상태 전이용 공유 pipeline. Repository 호출처에서 <c>Pipeline.Execute(() =&gt; ...)</c> 로 감싼다.</summary>
    public ResiliencePipeline Pipeline => _pipeline;

    private static bool IsTransient(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && IsTransient(sql);

    private static bool IsTransient(SqlException ex)
    {
        foreach (SqlError err in ex.Errors)
        {
            if (Array.IndexOf(TransientSqlErrorNumbers, err.Number) >= 0) return true;
        }
        return false;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Model;
using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// 분산 락 전용 Redis 관리자 베이스 클래스
    /// </summary>
    /// <typeparam name="TKey">락 키의 타입 (Guid, string, 복합 객체 등)</typeparam>
    public abstract class RedisLockManager<TKey> : RedisManagerBase
    {
        protected readonly RedisFailurePolicy FailurePolicy;

        protected abstract string KeyPrefix { get; }

        protected RedisLockManager(
            ILogger logger,
            IConnectionMultiplexer redisConnection,
            IConfiguration configuration,
            RedisFailurePolicy failurePolicy = RedisFailurePolicy.FailOnError)
            : base(logger, redisConnection, configuration)
        {
            FailurePolicy = failurePolicy;

            if (failurePolicy == RedisFailurePolicy.AllowOnError)
            {
                Logger.LogWarning(
                    AppLog.Log($"[{ManagerName}] CRITICAL: Running with AllowOnError policy. Duplicate executions possible during Redis outage."));
            }
        }

        /// <summary>
        /// 키 생성 로직 (하위 클래스에서 구현)
        /// </summary>
        protected abstract string GenerateKey(TKey key);

        /// <summary>
        /// 락 획득 시도
        /// </summary>
        /// <returns>true: 락 획득 성공, false: 이미 락이 존재</returns>
        public virtual async Task<bool> TryAcquireLockAsync(TKey key, string lockValue = null)
        {
            var redisKey = GenerateKey(key);
            var value = lockValue ?? $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}|{Environment.MachineName}";

            try
            {
                bool acquired = await Redis.StringSetAsync(
                    redisKey,
                    value,
                    TTL,
                    when: When.NotExists);

                if (acquired)
                {
                    Logger.LogInformation(
                        AppLog.Log($"[{ManagerName}] Lock acquired: Key={{Key}}"),
                        redisKey);
                }
                else
                {
                    Logger.LogWarning(
                        AppLog.Log($"[{ManagerName}] Lock already exists: Key={{Key}}"),
                        redisKey);
                }

                return acquired;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error acquiring lock: Key={{Key}}"),
                    redisKey);

                // 실패 정책 적용
                return FailurePolicy == RedisFailurePolicy.AllowOnError;
            }
        }

        /// <summary>
        /// 락 해제
        /// </summary>
        /// <returns>true: 락 해제 성공, false: 락이 존재하지 않음</returns>
        public virtual async Task<bool> ReleaseLockAsync(TKey key)
        {
            var redisKey = GenerateKey(key);

            try
            {
                bool deleted = await Redis.KeyDeleteAsync(redisKey);

                if (deleted)
                {
                    Logger.LogInformation(
                        AppLog.Log($"[{ManagerName}] Lock released: Key={{Key}}"),
                        redisKey);
                }
                else
                {
                    Logger.LogWarning(
                        AppLog.Log($"[{ManagerName}] No lock found to release: Key={{Key}}"),
                        redisKey);
                }

                return deleted;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error releasing lock: Key={{Key}}"),
                    redisKey);

                // 락 해제 실패 시 false 반환 (정책 무관)
                return false;
            }
        }

        /// <summary>
        /// 락 존재 여부 확인
        /// </summary>
        public virtual async Task<bool> IsLockedAsync(TKey key)
        {
            var redisKey = GenerateKey(key);

            try
            {
                bool exists = await Redis.KeyExistsAsync(redisKey);

                Logger.LogDebug(
                    AppLog.Log($"[{ManagerName}] Lock check: Key={{Key}}, Locked={{Locked}}"),
                    redisKey, exists);

                return exists;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error checking lock: Key={{Key}}"),
                    redisKey);

                // 에러 시 보수적으로 false 반환
                return false;
            }
        }

        /// <summary>
        /// 락의 남은 TTL 조회
        /// </summary>
        /// <returns>남은 TTL, 키가 없으면 null</returns>
        public virtual async Task<TimeSpan?> GetLockTTLAsync(TKey key)
        {
            var redisKey = GenerateKey(key);

            try
            {
                return await Redis.KeyTimeToLiveAsync(redisKey);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error getting TTL: Key={{Key}}"),
                    redisKey);

                return null;
            }
        }

        /// <summary>
        /// 패턴 매칭으로 모든 락 조회 (모니터링 용도)
        /// </summary>
        public virtual async Task<Dictionary<string, string>> GetAllLocksAsync(string pattern = null)
        {
            var result = new Dictionary<string, string>();
            var searchPattern = pattern ?? $"{KeyPrefix}*";

            try
            {
                var server = RedisConnection.GetServer(
                    RedisConnection.GetEndPoints().First());

                var keys = server.Keys(pattern: searchPattern).ToList();

                foreach (var key in keys)
                {
                    var value = await Redis.StringGetAsync(key);
                    if (value.HasValue)
                        result[key.ToString()] = value.ToString();
                }

                Logger.LogInformation(
                    AppLog.Log($"[{ManagerName}] Retrieved {{Count}} locks with pattern={{Pattern}}"),
                    result.Count, searchPattern);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error retrieving locks with pattern={{Pattern}}"),
                    searchPattern);
            }

            return result;
        }

        /// <summary>
        /// 패턴 매칭으로 락 일괄 삭제 (관리/유지보수 용도)
        /// </summary>
        public virtual async Task<int> ClearLocksAsync(string pattern = null)
        {
            var searchPattern = pattern ?? $"{KeyPrefix}*";

            try
            {
                var server = RedisConnection.GetServer(
                    RedisConnection.GetEndPoints().First());

                var keys = server.Keys(pattern: searchPattern).ToArray();

                if (keys.Length > 0)
                {
                    await Redis.KeyDeleteAsync(keys);

                    Logger.LogWarning(
                        AppLog.Log($"[{ManagerName}] Cleared {{Count}} locks with pattern={{Pattern}}"),
                        keys.Length, searchPattern);
                }

                return keys.Length;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error clearing locks with pattern={{Pattern}}"),
                    searchPattern);

                return 0;
            }
        }
    }
}
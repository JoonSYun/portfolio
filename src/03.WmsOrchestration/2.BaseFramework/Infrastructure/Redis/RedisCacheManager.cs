using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// 캐시 전용 Redis 관리자 베이스 클래스
    /// 값 저장/조회/삭제 기능 제공
    /// </summary>
    /// <typeparam name="TKey">캐시 키의 타입</typeparam>
    public abstract class RedisCacheManager<TKey> : RedisManagerBase
    {
        protected abstract string KeyPrefix { get; }

        protected RedisCacheManager(
            ILogger logger,
            IConnectionMultiplexer redisConnection,
            IConfiguration configuration)
            : base(logger, redisConnection, configuration)
        {
        }

        /// <summary>
        /// 키 생성 로직 (하위 클래스에서 구현)
        /// </summary>
        protected abstract string GenerateKey(TKey key);

        /// <summary>
        /// 캐시 설정 (덮어쓰기)
        /// </summary>
        public virtual async Task<bool> SetAsync(TKey key, string value, TimeSpan? customTTL = null)
        {
            var redisKey = GenerateKey(key);
            var ttl = customTTL ?? TTL;

            try
            {
                await Redis.StringSetAsync(redisKey, value, ttl);

                Logger.LogInformation(
                    AppLog.Log($"[{ManagerName}] Cache set: Key={{Key}}, TTL={{TTL}}"),
                    redisKey, ttl);

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error setting cache: Key={{Key}}"),
                    redisKey);

                return false;
            }
        }

        /// <summary>
        /// 캐시 조회
        /// </summary>
        /// <returns>캐시된 값, 없으면 null</returns>
        public virtual async Task<string> GetAsync(TKey key)
        {
            var redisKey = GenerateKey(key);

            try
            {
                var value = await Redis.StringGetAsync(redisKey);

                Logger.LogDebug(
                    AppLog.Log($"[{ManagerName}] Cache get: Key={{Key}}, Found={{Found}}"),
                    redisKey, value.HasValue);

                return value.HasValue ? value.ToString() : null;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error getting cache: Key={{Key}}"),
                    redisKey);

                return null;
            }
        }

        /// <summary>
        /// 캐시 삭제
        /// </summary>
        /// <returns>true: 삭제 성공, false: 키가 존재하지 않음</returns>
        public virtual async Task<bool> DeleteAsync(TKey key)
        {
            var redisKey = GenerateKey(key);

            try
            {
                bool deleted = await Redis.KeyDeleteAsync(redisKey);

                if (deleted)
                {
                    Logger.LogInformation(
                        AppLog.Log($"[{ManagerName}] Cache deleted: Key={{Key}}"),
                        redisKey);
                }
                else
                {
                    Logger.LogDebug(
                        AppLog.Log($"[{ManagerName}] No cache found to delete: Key={{Key}}"),
                        redisKey);
                }

                return deleted;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error deleting cache: Key={{Key}}"),
                    redisKey);

                return false;
            }
        }

        /// <summary>
        /// 캐시 존재 여부 확인
        /// </summary>
        public virtual async Task<bool> ExistsAsync(TKey key)
        {
            var redisKey = GenerateKey(key);

            try
            {
                bool exists = await Redis.KeyExistsAsync(redisKey);

                Logger.LogDebug(
                    AppLog.Log($"[{ManagerName}] Cache exists check: Key={{Key}}, Exists={{Exists}}"),
                    redisKey, exists);

                return exists;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log($"[{ManagerName}] Error checking cache existence: Key={{Key}}"),
                    redisKey);

                return false;
            }
        }

        /// <summary>
        /// 캐시의 남은 TTL 조회
        /// </summary>
        public virtual async Task<TimeSpan?> GetTTLAsync(TKey key)
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
    }
}
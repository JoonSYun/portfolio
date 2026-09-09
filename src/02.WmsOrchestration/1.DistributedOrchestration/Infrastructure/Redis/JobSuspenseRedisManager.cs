using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Job Suspense 관리용 Redis Manager
    /// Guid 기반 단순 락 메커니즘
    /// </summary>
    public class JobSuspenseRedisManager : RedisLockManager<Guid>
    {
        protected override string ManagerName => "JobSuspenseRedisManager";
        protected override string KeyPrefix => "job:";
        protected override string ConfigurationTTLKey => "RedisTTL:JobSuspense";
        protected override TimeSpan DefaultTTL => TimeSpan.FromHours(24);

        public JobSuspenseRedisManager(
            ILogger<JobSuspenseRedisManager> logger,
            IConnectionMultiplexer redisConnection,
            IConfiguration configuration)
            : base(logger, redisConnection, configuration)
        {
        }

        protected override string GenerateKey(Guid key)
        {
            return $"{KeyPrefix}{key}:suspense";
        }

        // ====================================================================
        // 기존 Public API 유지 (호출부 코드 변경 불필요)
        // ====================================================================

        /// <summary>
        /// Atomic operation : only creates the key if it does not already exist.
        /// return : true if the key was created, false if it already existed.
        /// </summary>
        public async Task<bool> AddAsync(Guid id)
        {
            return await TryAcquireLockAsync(id, "1");
        }

        /// <summary>
        /// Checks whether the key for the given Id exists.
        /// return : true if the key exists, false otherwise.
        /// </summary>
        public async Task<bool> ExistsAsync(Guid id)
        {
            return await IsLockedAsync(id);
        }

        /// <summary>
        /// Deletes the key for the given Id.
        /// return : true if the key was deleted, false if it did not exist.
        /// </summary>
        public async Task<bool> DeleteAsync(Guid id)
        {
            return await ReleaseLockAsync(id);
        }
    }
}
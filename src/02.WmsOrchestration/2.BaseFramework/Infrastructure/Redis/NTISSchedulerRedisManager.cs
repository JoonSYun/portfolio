using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Model;
using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// NTISSchedulerRedisManager
    /// ------------------------
    /// Provides a distributed lock mechanism to prevent
    /// duplicate scheduler executions across multiple servers.
    ///
    /// Migration note:
    ///   Replaced in-memory ConcurrentDictionary with Redis to
    ///   support multi-instance environments.
    ///
    /// WMS 특성상 중복 실행 방지를 위해 FailOnError 정책 사용
    /// </summary>
    public class NTISSchedulerRedisManager : RedisLockManager<SchedulerLockKey>
    {
        protected override string ManagerName => "SchedulerLock";
        protected override string KeyPrefix => "scheduler:lock:";
        protected override string ConfigurationTTLKey => "Redis:Scheduler:TTL";

        // 기본 TTL: 30분 (기존 _ttlMinutes와 동일)
        // 추 후 하트비트 메커니즘 도입 필요
        // config로 빼야됨
        protected override TimeSpan DefaultTTL => TimeSpan.FromMinutes(30);

        public NTISSchedulerRedisManager(
            ILogger<NTISSchedulerRedisManager> logger,
            IConnectionMultiplexer redisConnection,
            IConfiguration configuration)
            : base(logger, redisConnection, configuration, RedisFailurePolicy.FailOnError)
        {
        }

        protected override string GenerateKey(SchedulerLockKey key)
        {
            return $"{KeyPrefix}{key.BCode}:{key.ApiCode}:{key.IfCode}";
        }

        // ====================================================================
        // 기존 Public API 유지 (호출부 코드 변경 불필요)
        // ====================================================================

        /// <summary>
        /// Try to acquire a scheduler execution lock.
        ///
        /// Returns:
        ///   true  → lock acquired, scheduler may run
        ///   false → another scheduler is already running OR Redis error occurred
        ///
        /// Lock is scoped by (BCODE, API_CODE, IF_CODE).
        /// 
        /// 변경사항: Redis 장애 시 false 반환 (중복 실행 방지)
        /// </summary>
        public async Task<bool> TryAcquireLockAsync(string hostSystem, string bcode, string apiCode, string ifCode)
        {
            var key = new SchedulerLockKey(hostSystem, bcode, apiCode, ifCode);
            var lockValue = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}|{Environment.MachineName}";

            return await TryAcquireLockAsync(key, lockValue);
        }

        /// <summary>
        /// Release a previously acquired scheduler lock.
        ///
        /// Safe to call even if the key does not exist.
        /// </summary>
        public async Task<bool> ReleaseLockAsync(string hostSystem, string bcode, string apiCode, string ifCode)
        {
            var key = new SchedulerLockKey(hostSystem, bcode, apiCode, ifCode);
            return await ReleaseLockAsync(key);
        }

        /// <summary>
        /// Check whether a lock exists for the given scheduler key.
        /// </summary>
        public async Task<bool> IsLockedAsync(string hostSystem, string bcode, string apiCode, string ifCode)
        {
            var key = new SchedulerLockKey(hostSystem, bcode, apiCode, ifCode);
            return await IsLockedAsync(key);
        }

        /// <summary>
        /// Get remaining TTL for the given lock key.
        /// Returns null if key does not exist.
        /// </summary>
        public async Task<TimeSpan?> GetLockTTLAsync(string hostSystem, string bcode, string apiCode, string ifCode)
        {
            var key = new SchedulerLockKey(hostSystem, bcode, apiCode, ifCode);
            return await GetLockTTLAsync(key);
        }

        /// <summary>
        /// Retrieve all active scheduler locks (monitoring purpose).
        /// </summary>
        public async Task<Dictionary<string, string>> GetAllLocksAsync()
        {
            return await GetAllLocksAsync(null);
        }

        /// <summary>
        /// Bulk-clear locks by pattern (admin/maintenance use).
        /// </summary>
        public async Task<int> ClearLocksAsync(string bcode = null)
        {
            var pattern = string.IsNullOrEmpty(bcode)
                ? $"{KeyPrefix}*"
                : $"{KeyPrefix}{bcode}:*";

            return await base.ClearLocksAsync(pattern);
        }
    }

    /// <summary>
    /// 스케줄러 락의 복합 키
    /// (BCode, ApiCode, IfCode)로 구성
    /// </summary>
    public record SchedulerLockKey(string hostSystem, string BCode, string ApiCode, string IfCode)
    {
        public override string ToString() => $"{hostSystem}:{BCode}:{ApiCode}:{IfCode}";
    }
}
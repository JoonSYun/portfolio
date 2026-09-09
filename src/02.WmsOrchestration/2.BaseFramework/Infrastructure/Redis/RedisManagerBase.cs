using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Redis 관리자의 최상위 베이스 클래스
    /// Redis 연결, 로깅, TTL 설정 등 공통 인프라만 제공
    /// </summary>
    public abstract class RedisManagerBase
    {
        protected readonly ILogger Logger;
        protected readonly IDatabase Redis;
        protected readonly IConnectionMultiplexer RedisConnection;
        protected readonly TimeSpan TTL;

        protected abstract string ManagerName { get; }
        protected abstract string ConfigurationTTLKey { get; }
        protected abstract TimeSpan DefaultTTL { get; }

        protected RedisManagerBase(
            ILogger logger,
            IConnectionMultiplexer redisConnection,
            IConfiguration configuration)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            RedisConnection = redisConnection ?? throw new ArgumentNullException(nameof(redisConnection));
            Redis = redisConnection.GetDatabase();
            TTL = ResolveTTL(configuration);
        }

        /// <summary>
        /// TTL 설정 우선순위:
        /// 1. "Redis:XXX:TTL" (TimeSpan 문자열)
        /// 2. "Redis:XXX:TTLHour" (레거시 호환)
        /// 3. DefaultTTL
        /// </summary>
        private TimeSpan ResolveTTL(IConfiguration configuration)
        {
            // 1. TimeSpan 직접 설정 시도
            var ttlString = configuration.GetValue<string>(ConfigurationTTLKey);
            if (!string.IsNullOrEmpty(ttlString) && TimeSpan.TryParse(ttlString, out var ttl))
            {
                Logger.LogDebug(
                    AppLog.Log($"[{ManagerName}] TTL loaded from config: {{TTL}}"),
                    ttl);
                return ttl;
            }

            // 2. 레거시 Hour 설정 시도
            var legacyKey = $"{ConfigurationTTLKey}Hour";
            var hours = configuration.GetValue<int?>(legacyKey);
            if (hours.HasValue && hours.Value > 0)
            {
                Logger.LogWarning(
                    AppLog.Log($"[{ManagerName}] Using legacy TTLHour config: {{Hours}} hours. Consider migrating to TTL (TimeSpan format)."),
                    hours.Value);
                return TimeSpan.FromHours(hours.Value);
            }

            // 3. 기본값
            Logger.LogDebug(
                AppLog.Log($"[{ManagerName}] Using default TTL: {{TTL}}"),
                DefaultTTL);
            return DefaultTTL;
        }
    }
}
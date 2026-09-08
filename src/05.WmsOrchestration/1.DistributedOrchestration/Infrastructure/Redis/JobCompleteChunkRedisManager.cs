using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Manages Redis-based job chunk completion counters.
    /// This component is responsible for:
    ///   - Initializing job-level completion counters,
    ///   - Atomically increasing completed chunk count using Lua scripts,
    ///   - Determining job completion state,
    ///   - Clearing related Redis keys after job completion.
    /// </summary>
    public class JobCompleteChunkRedisManager
    {
        private readonly ILogger<JobCompleteChunkRedisManager> _logger;
        private readonly IDatabase _redis;

        private const string JobKeyFormat = "job:{0}:completedCount";

        private readonly int _jobCompleteChunkCountTTL_Hour;

        public JobCompleteChunkRedisManager(
            ILogger<JobCompleteChunkRedisManager> logger,
            IConnectionMultiplexer redisConnection,
            IConfiguration configuration)
        {
            _logger = logger;
            _redis = redisConnection.GetDatabase();

            _jobCompleteChunkCountTTL_Hour =
                configuration.GetValue<int>("RedisTTL:JobCompleteChunkCount_Hour", 24);
        }

        /// <summary>
        /// Creates a Redis key for the given JobId and sets the initial completed count to 0.
        /// </summary>
        public async Task<bool> AddJobCompleteChunkCountRedisAsync(Guid jobId)
        {
            var key = string.Format(JobKeyFormat, jobId.ToString());

            try
            {
                bool created = await _redis.StringSetAsync(key, 0, when: When.NotExists);

                if (created)
                {
                    _logger.LogInformation(
                        AppLog.Log("[JobCompleteChunkRedisManager] Created Redis key for JobId={JobId} with initial value 0"),
                        jobId);
                }
                else
                {
                    _logger.LogInformation(
                        AppLog.Log("[JobCompleteChunkRedisManager] Redis key already exists for JobId={JobId}"),
                        jobId);
                }

                return created;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    AppLog.Log("[JobCompleteChunkRedisManager] Error creating Redis key for JobId={JobId}"),
                    jobId);

                throw;
            }
        }

        /// <summary>
        /// Atomically increments completed chunk count using Lua and checks if the job is completed.
        /// If the key does not exist, this method initializes it with value 0 (temporary behavior; may be revised later).
        /// </summary>
        public async Task<(bool Exists, bool Completed)> IncrementAndCheckCompletedAsync(Guid jobId, int totalChunkCount)
        {
            var key = string.Format(JobKeyFormat, jobId);

            // Lua 스크립트:
            // 1) 키 없으면 {0,0} 반환
            // 2) 키 있으면 atomic increment 후 completed 여부 반환
            const string script = @"
                                    local current = redis.call('GET', KEYS[1])
                                    if current == false then
                                        return {0, 0} -- Exists = false, Completed = false
                                    end

                                    current = redis.call('INCR', KEYS[1])
                                    if tonumber(current) >= tonumber(ARGV[1]) then
                                        return {1, 1} -- Exists = true, Completed = true
                                    else
                                        return {1, 0} -- Exists = true, Completed = false
                                    end
                                ";

            var result = (RedisResult[])await _redis.ScriptEvaluateAsync(
                script,
                new RedisKey[] { key },
                new RedisValue[] { totalChunkCount }
            );

            bool exists = (int)result[0] == 1;
            bool completed = (int)result[1] == 1;

            // Completed 시 TTL 유지 로직 (필요 시)
            if (exists && completed)
            {
                await _redis.KeyExpireAsync(key, TimeSpan.FromHours(_jobCompleteChunkCountTTL_Hour));

                _logger.LogInformation(
                    AppLog.Log("[JobCompleteChunkRedisManager] JobId={JobId} completed. (Redis Key TTL refreshed)"),
                    jobId
                );
            }

            return (exists, completed);
        }


        /// <summary>
        /// Retrieves the number of chunks marked as completed for the given JobId.
        /// </summary>
        public async Task<long> GetCompletedCountAsync(Guid jobId)
        {
            var key = string.Format(JobKeyFormat, jobId);
            var value = await _redis.StringGetAsync(key);
            return value.HasValue ? (long)value : -1;
        }

        /// <summary>
        /// Checks whether the job's completed chunk count has reached the total chunk count.
        /// </summary>
        public async Task<bool> IsJobCompletedAsync(Guid jobId, int totalChunkCount)
        {
            long completedCount = await GetCompletedCountAsync(jobId);
            bool isCompleted = completedCount >= totalChunkCount;

            if (isCompleted)
            {
                _logger.LogInformation(
                    AppLog.Log("[JobCompleteChunkRedisManager] JobId={JobId} completed ({Completed}/{Total})"),
                    jobId,
                    completedCount,
                    totalChunkCount);
            }

            return isCompleted;
        }

        /// <summary>
        /// Invokes onJobComplete callback when job is completed, then clears the Redis key.
        /// </summary>
        public async Task<bool> TryCompleteJobAsync(Guid jobId, int totalChunkCount, Func<Task> onJobComplete)
        {
            bool isCompleted = await IsJobCompletedAsync(jobId, totalChunkCount);

            if (isCompleted)
            {
                await onJobComplete.Invoke();
                await ClearJobAsync(jobId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Deletes the job's Redis key for cleanup.
        /// </summary>
        public async Task ClearJobAsync(Guid jobId)
        {
            var key = string.Format(JobKeyFormat, jobId.ToString());
            await _redis.KeyDeleteAsync(key);

            _logger.LogInformation(
                AppLog.Log("[JobCompleteChunkRedisManager] Cleared Redis key for JobId={JobId}"),
                jobId);
        }
    }
}

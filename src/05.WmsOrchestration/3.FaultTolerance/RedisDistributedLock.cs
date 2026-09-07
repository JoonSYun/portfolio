using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.FaultTolerance;

/// <summary>
/// [담당업무 3] Redis 분산 락 — 여러 워커가 같은 자원(브랜드의 재고 스냅샷, 특정 주문)을 동시에 만지지 못하게.
///
/// 멱등성이 "같은 일을 두 번" 을 막는다면, 분산 락은 "다른 일이 같은 자원을 동시에" 를 막는다.
/// SET NX PX 로 획득, 소유 토큰을 검사하는 Lua 로 해제(남의 락을 풀지 않는다), 긴 작업은 갱신.
/// </summary>
public sealed class RedisDistributedLock
{
    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token then return redis.call('DEL', @key) else return 0 end");
    private static readonly LuaScript ExtendScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token then return redis.call('PEXPIRE', @key, @ttl) else return 0 end");

    private readonly IDatabase _redis;
    public RedisDistributedLock(IConnectionMultiplexer mux) => _redis = mux.GetDatabase();

    /// <summary>획득 실패 시 null. 호출자는 기다릴지, 건너뛸지, 재전달할지 결정한다.</summary>
    public async Task<LockHandle?> TryAcquireAsync(string resource, TimeSpan ttl, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var key = $"lock:{resource}";
        var token = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.Zero);

        do
        {
            if (await _redis.StringSetAsync(key, token, ttl, When.NotExists))
                return new LockHandle(this, key, token, ttl);
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(50, ct);
        } while (!ct.IsCancellationRequested);

        return null;
    }

    internal Task<bool> ReleaseAsync(string key, string token) =>
        _redis.ScriptEvaluateAsync(ReleaseScript, new { key = (RedisKey)key, token }).ContinueWith(t => (int)t.Result == 1);

    internal Task<bool> ExtendAsync(string key, string token, TimeSpan ttl) =>
        _redis.ScriptEvaluateAsync(ExtendScript, new { key = (RedisKey)key, token, ttl = (long)ttl.TotalMilliseconds }).ContinueWith(t => (int)t.Result == 1);

    public sealed class LockHandle : IAsyncDisposable
    {
        private readonly RedisDistributedLock _owner;
        private readonly string _key, _token;
        private readonly TimeSpan _ttl;
        internal LockHandle(RedisDistributedLock owner, string key, string token, TimeSpan ttl) { _owner = owner; _key = key; _token = token; _ttl = ttl; }

        /// <summary>긴 작업 중간에 호출 — TTL 만료로 락이 풀리며 다른 워커가 끼어드는 것을 막는다.</summary>
        public Task<bool> ExtendAsync() => _owner.ExtendAsync(_key, _token, _ttl);
        public async ValueTask DisposeAsync() => await _owner.ReleaseAsync(_key, _token);
    }
}

/* 사용 예 — 재고 스냅샷 계산은 브랜드당 한 워커만:
   await using var handle = await _lock.TryAcquireAsync($"stock-snapshot:{tenant}", ttl: TimeSpan.FromMinutes(5), wait: TimeSpan.FromSeconds(2));
   if (handle is null) throw new TransientStepException("다른 워커가 처리 중"); // → 지연 재전달
*/

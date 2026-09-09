using StackExchange.Redis;

namespace Portfolio.WmsOrchestration.FaultTolerance;

public interface IIdempotencyStore
{
    Task<IdempotencyClaim> TryClaimAsync(string messageId, string businessKey, CancellationToken ct);
}

/// <summary>클레임 결과. 처리 후 Complete(영구 기록) 또는 Release(재시도 허용) 중 하나를 반드시 호출한다.</summary>
public sealed class IdempotencyClaim
{
    private readonly Func<Task> _complete, _release;
    public bool Acquired { get; }
    public string? Reason { get; }
    internal IdempotencyClaim(bool acquired, string? reason, Func<Task> complete, Func<Task> release)
    { Acquired = acquired; Reason = reason; _complete = complete; _release = release; }
    public Task CompleteAsync() => _complete();
    public Task ReleaseAsync() => _release();
    internal static IdempotencyClaim Rejected(string reason) => new(false, reason, () => Task.CompletedTask, () => Task.CompletedTask);
}

/// <summary>
/// [담당업무 3] 이중 멱등성 체크 (Redis).
///
/// 중복은 두 층에서 생긴다:
///   ① 전송 계층 — 브로커 재전달, 컨슈머 ack 유실 → 같은 MessageId 가 두 번 온다
///   ② 업무 계층 — 상류가 같은 이벤트를 다시 발행, 운영자가 재처리 버튼을 두 번 누름 → MessageId 는 다른데 같은 일
/// 하나만 막으면 나머지가 뚫린다. 그래서 둘 다 본다.
///
/// 처리 중(in-flight) 클레임은 TTL 을 짧게 두어 워커가 죽어도 영원히 잠기지 않고,
/// 완료 기록은 길게 남겨 뒤늦은 중복을 걸러낸다.
/// </summary>
public sealed class RedisDoubleIdempotencyStore : IIdempotencyStore
{
    private static readonly TimeSpan InFlightTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromDays(7);
    private readonly IDatabase _redis;

    public RedisDoubleIdempotencyStore(IConnectionMultiplexer mux) => _redis = mux.GetDatabase();

    public async Task<IdempotencyClaim> TryClaimAsync(string messageId, string businessKey, CancellationToken ct)
    {
        var msgKey = $"idem:msg:{messageId}";
        var bizKey = $"idem:biz:{businessKey}";

        // ① MessageId — 이미 본 메시지면 즉시 거절
        if (!await _redis.StringSetAsync(msgKey, "inflight", InFlightTtl, When.NotExists))
            return IdempotencyClaim.Rejected("MessageId 중복");

        // ② 비즈니스 키 — 다른 메시지가 같은 일을 이미 했거나 하는 중이면 거절
        if (!await _redis.StringSetAsync(bizKey, messageId, InFlightTtl, When.NotExists))
        {
            await _redis.KeyDeleteAsync(msgKey);   // 첫 클레임은 되돌린다
            return IdempotencyClaim.Rejected("비즈니스 키 중복");
        }

        return new IdempotencyClaim(true, null,
            complete: async () =>
            {
                // 완료 — 둘 다 오래 남긴다
                await _redis.StringSetAsync(msgKey, "done", CompletedTtl);
                await _redis.StringSetAsync(bizKey, "done", CompletedTtl);
            },
            release: async () =>
            {
                // 실패 — 둘 다 풀어 재시도가 통과하게 한다
                await _redis.KeyDeleteAsync(new RedisKey[] { msgKey, bizKey });
            });
    }
}

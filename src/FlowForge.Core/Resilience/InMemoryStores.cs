using System.Collections.Concurrent;
using FlowForge.Abstractions.Resilience;

namespace FlowForge.Core.Resilience;

/// <summary>
/// In-memory idempotency/inbox store. A claim is a distributed lock in production
/// (e.g. Redis SET NX); here it is an atomic dictionary insert. Claims are released
/// on failure so retries can re-run the same key.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> _claimed = new();

    public Task<bool> TryClaimAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_claimed.TryAdd(key, 0));

    public Task CompleteAsync(string key, CancellationToken ct = default) =>
        Task.CompletedTask; // completed keys stay claimed → permanent dedup

    public Task ReleaseAsync(string key, CancellationToken ct = default)
    {
        _claimed.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory transactional outbox. State-plus-intent is staged here; a dispatcher
/// drains unpublished rows onto the bus. Backed by a DB table in production so the
/// stage happens in the same transaction as the state change.
/// </summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<string, OutboxMessage> _messages = new();

    public Task StageAsync(OutboxMessage message, CancellationToken ct = default)
    {
        _messages[message.Id] = message;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> FetchUnpublishedAsync(int max = 50, CancellationToken ct = default)
    {
        var pending = _messages.Values
            .Where(m => m.PublishedAt is null)
            .OrderBy(m => m.CreatedAt)
            .Take(max)
            .ToList();
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(pending);
    }

    public Task MarkPublishedAsync(string id, DateTimeOffset publishedAt, CancellationToken ct = default)
    {
        if (_messages.TryGetValue(id, out var existing))
            _messages[id] = existing with { PublishedAt = publishedAt };
        return Task.CompletedTask;
    }
}

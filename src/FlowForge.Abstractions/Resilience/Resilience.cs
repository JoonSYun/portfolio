namespace FlowForge.Abstractions.Resilience;

/// <summary>
/// Deduplication guard. Backs both the inbound check (inbox: "have I already
/// consumed this message?") and the business check (idempotency key: "have I
/// already done this unit of work?").
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims the key. Returns true if this caller won the claim and
    /// should proceed; false if the key was already seen (skip as a duplicate).
    /// </summary>
    Task<bool> TryClaimAsync(string key, CancellationToken ct = default);

    /// <summary>Marks a claimed key as fully processed.</summary>
    Task CompleteAsync(string key, CancellationToken ct = default);

    /// <summary>Releases a claim so a failed attempt can be retried.</summary>
    Task ReleaseAsync(string key, CancellationToken ct = default);
}

/// <summary>A message staged in the outbox, to be published atomically-with-state.</summary>
public sealed record OutboxMessage
{
    public required string Id { get; init; }
    public required string JobKey { get; init; }
    public required string Payload { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Transactional outbox. State change and the intent to publish are written
/// together; a dispatcher later drains unpublished rows onto the bus. This is
/// what prevents "state committed but message lost" under partial failure.
/// </summary>
public interface IOutboxStore
{
    Task StageAsync(OutboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> FetchUnpublishedAsync(int max = 50, CancellationToken ct = default);
    Task MarkPublishedAsync(string id, DateTimeOffset publishedAt, CancellationToken ct = default);
}

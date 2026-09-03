using System.Collections.Concurrent;
using FlowForge.Abstractions.Operations;

namespace FlowForge.Core.Operations;

/// <summary>In-memory execution history (a table in production), newest-first on query.</summary>
public sealed class InMemoryExecutionStore : IExecutionStore
{
    private readonly ConcurrentDictionary<string, ExecutionRecord> _records = new();

    public Task AppendAsync(ExecutionRecord record, CancellationToken ct = default)
    {
        _records[record.ExecutionId] = record;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ExecutionRecord record, CancellationToken ct = default)
    {
        _records[record.ExecutionId] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExecutionRecord>> QueryAsync(
        string? jobKey = null, string? tenantId = null, int limit = 100, CancellationToken ct = default)
    {
        IEnumerable<ExecutionRecord> query = _records.Values;
        if (jobKey is not null)
            query = query.Where(r => r.JobKey == jobKey);
        if (tenantId is not null)
            query = query.Where(r => r.TenantId == tenantId);

        var result = query
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExecutionRecord>>(result);
    }
}

using System.Collections.Concurrent;
using FlowForge.Abstractions.Configuration;

namespace FlowForge.Core.Configuration;

/// <summary>
/// In-memory <see cref="IConfigStore"/>. Stands in for the two production tables
/// (job_definition / tenant_override) that make config the single source of truth,
/// so a new tenant or domain goes live by inserting a row — no deploy.
/// </summary>
public sealed class InMemoryConfigStore : IConfigStore
{
    private readonly ConcurrentDictionary<string, JobDefinition> _definitions = new();
    private readonly ConcurrentDictionary<(string JobKey, string TenantId), TenantOverride> _overrides = new();

    public Task<IReadOnlyList<JobDefinition>> GetDefinitionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JobDefinition>>(_definitions.Values.ToList());

    public Task<JobDefinition?> GetDefinitionAsync(string jobKey, CancellationToken ct = default) =>
        Task.FromResult(_definitions.GetValueOrDefault(jobKey));

    public Task<IReadOnlyList<TenantOverride>> GetOverridesAsync(string jobKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TenantOverride>>(
            _overrides.Values.Where(o => o.JobKey == jobKey).ToList());

    public Task UpsertDefinitionAsync(JobDefinition definition, CancellationToken ct = default)
    {
        _definitions[definition.JobKey] = definition;
        return Task.CompletedTask;
    }

    public Task UpsertOverrideAsync(TenantOverride tenantOverride, CancellationToken ct = default)
    {
        _overrides[(tenantOverride.JobKey, tenantOverride.TenantId)] = tenantOverride;
        return Task.CompletedTask;
    }
}

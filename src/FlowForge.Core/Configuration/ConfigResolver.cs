using FlowForge.Abstractions.Configuration;

namespace FlowForge.Core.Configuration;

/// <summary>
/// Merges a <see cref="JobDefinition"/> (domain defaults) with a matching
/// <see cref="TenantOverride"/> to produce the effective config. This is the whole
/// of the two-tier inheritance model: invariant rules stay on the definition,
/// tenants override only the fields that differ, and parameters are shallow-merged.
/// </summary>
public sealed class ConfigResolver : IConfigResolver
{
    private readonly IConfigStore _store;

    public ConfigResolver(IConfigStore store) => _store = store;

    public async Task<EffectiveJobConfig> ResolveAsync(string jobKey, string tenantId, CancellationToken ct = default)
    {
        var definition = await _store.GetDefinitionAsync(jobKey, ct)
            ?? throw new KeyNotFoundException($"No job definition registered for '{jobKey}'.");
        var overrides = await _store.GetOverridesAsync(jobKey, ct);
        var tenantOverride = overrides.FirstOrDefault(o => o.TenantId == tenantId);
        return Merge(definition, tenantId, tenantOverride);
    }

    public async Task<IReadOnlyList<EffectiveJobConfig>> ResolveAllAsync(string jobKey, CancellationToken ct = default)
    {
        var definition = await _store.GetDefinitionAsync(jobKey, ct)
            ?? throw new KeyNotFoundException($"No job definition registered for '{jobKey}'.");
        var overrides = (await _store.GetOverridesAsync(jobKey, ct)).ToDictionary(o => o.TenantId);

        var tenants = definition.Tenants.Count > 0
            ? definition.Tenants
            : new[] { "default" };

        return tenants
            .Select(t => Merge(definition, t, overrides.GetValueOrDefault(t)))
            .ToList();
    }

    private static EffectiveJobConfig Merge(JobDefinition def, string tenantId, TenantOverride? ovr)
    {
        var parameters = new Dictionary<string, string>(def.Parameters);
        if (ovr?.ParameterOverrides is { } overrides)
        {
            foreach (var (key, value) in overrides)
                parameters[key] = value;
        }

        return new EffectiveJobConfig
        {
            JobKey = def.JobKey,
            TenantId = tenantId,
            Cron = ovr?.Cron ?? def.Cron,
            Timeout = ovr?.Timeout ?? def.Timeout,
            Retry = ovr?.Retry ?? def.Retry,
            Enabled = ovr?.Enabled ?? def.Enabled,
            Parameters = parameters
        };
    }
}

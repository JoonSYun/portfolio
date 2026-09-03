namespace FlowForge.Abstractions.Configuration;

/// <summary>How a retry policy spaces out attempts.</summary>
public enum BackoffStrategy
{
    Fixed,
    Exponential
}

/// <summary>
/// Declarative retry policy. Kept as data (not code) so it can live in the
/// configuration store and be overridden per tenant without a redeploy.
/// </summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public BackoffStrategy Backoff { get; init; } = BackoffStrategy.Exponential;

    public static RetryPolicy Default { get; } = new();

    /// <summary>Delay before the given (1-based) attempt.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt <= 1) return TimeSpan.Zero;
        return Backoff switch
        {
            BackoffStrategy.Fixed => BaseDelay,
            BackoffStrategy.Exponential => BaseDelay * Math.Pow(2, attempt - 2),
            _ => BaseDelay
        };
    }
}

/// <summary>
/// Domain-level defaults for a job type. This is the top of the two-tier
/// inheritance model: the invariant execution rules live here, once per domain,
/// regardless of how many tenants fan out from it.
/// </summary>
public sealed record JobDefinition
{
    public required string JobKey { get; init; }

    /// <summary>Cron expression driving the schedule (5- or 6-field).</summary>
    public required string Cron { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
    public bool Enabled { get; init; } = true;

    /// <summary>Default parameters passed to the handler.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Tenants this domain fans out to at fire time. Registration cost stays at
    /// "one schedule per domain"; execution fans out to one run per tenant.
    /// </summary>
    public IReadOnlyList<string> Tenants { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Per-tenant override. Every field is nullable: a null field inherits the
/// domain default. This is field-level inheritance — a tenant overrides only
/// what actually differs, never a full copy of the definition.
/// </summary>
public sealed record TenantOverride
{
    public required string JobKey { get; init; }
    public required string TenantId { get; init; }

    public string? Cron { get; init; }
    public TimeSpan? Timeout { get; init; }
    public RetryPolicy? Retry { get; init; }
    public bool? Enabled { get; init; }

    /// <summary>Parameter keys to override or add for this tenant.</summary>
    public IReadOnlyDictionary<string, string>? ParameterOverrides { get; init; }
}

/// <summary>
/// The merged, ready-to-execute configuration for one (jobKey, tenant) pair.
/// Produced by <c>IConfigResolver</c> by layering a <see cref="TenantOverride"/>
/// on top of a <see cref="JobDefinition"/>.
/// </summary>
public sealed record EffectiveJobConfig
{
    public required string JobKey { get; init; }
    public required string TenantId { get; init; }
    public required string Cron { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required RetryPolicy Retry { get; init; }
    public required bool Enabled { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
}

/// <summary>
/// Storage for job definitions and tenant overrides. In this reference build the
/// implementation is in-memory; in production it is a single database table pair
/// (the "single source of truth" the schedule config was moved out of code into).
/// </summary>
public interface IConfigStore
{
    Task<IReadOnlyList<JobDefinition>> GetDefinitionsAsync(CancellationToken ct = default);
    Task<JobDefinition?> GetDefinitionAsync(string jobKey, CancellationToken ct = default);
    Task<IReadOnlyList<TenantOverride>> GetOverridesAsync(string jobKey, CancellationToken ct = default);
    Task UpsertDefinitionAsync(JobDefinition definition, CancellationToken ct = default);
    Task UpsertOverrideAsync(TenantOverride tenantOverride, CancellationToken ct = default);
}

/// <summary>Resolves the effective config by merging domain defaults with tenant overrides.</summary>
public interface IConfigResolver
{
    Task<EffectiveJobConfig> ResolveAsync(string jobKey, string tenantId, CancellationToken ct = default);

    /// <summary>Resolves the effective config for every tenant the domain fans out to.</summary>
    Task<IReadOnlyList<EffectiveJobConfig>> ResolveAllAsync(string jobKey, CancellationToken ct = default);
}

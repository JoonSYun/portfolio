using FlowForge.Abstractions.Configuration;

namespace FlowForge.Api;

/// <summary>
/// Seeds job definitions and tenant overrides so the platform does something the
/// moment it starts. In production this data lives in a database — the same rows,
/// editable without a deploy. Note how tenants override only what differs.
/// </summary>
public static class SeedData
{
    public static async Task ApplyAsync(IConfigStore store, CancellationToken ct = default)
    {
        // --- inventory.snapshot: a simple job, two tenants, per-tenant warehouse. ---
        await store.UpsertDefinitionAsync(new JobDefinition
        {
            JobKey = "inventory.snapshot",
            Cron = "*/15 * * * * *", // every 15 seconds
            Timeout = TimeSpan.FromSeconds(10),
            Parameters = new Dictionary<string, string> { ["warehouse"] = "DEFAULT" },
            Tenants = new[] { "seoul", "busan" }
        }, ct);
        await store.UpsertOverrideAsync(new TenantOverride
        {
            JobKey = "inventory.snapshot",
            TenantId = "seoul",
            ParameterOverrides = new Dictionary<string, string> { ["warehouse"] = "SEL-01" }
        }, ct);
        await store.UpsertOverrideAsync(new TenantOverride
        {
            JobKey = "inventory.snapshot",
            TenantId = "busan",
            // busan runs on a slower cadence — override only the cron.
            Cron = "*/30 * * * * *",
            ParameterOverrides = new Dictionary<string, string> { ["warehouse"] = "PUS-01" }
        }, ct);

        // --- order.sync: a chunked batch job across three brands. ---
        await store.UpsertDefinitionAsync(new JobDefinition
        {
            JobKey = "order.sync",
            Cron = "*/20 * * * * *", // every 20 seconds
            Timeout = TimeSpan.FromSeconds(30),
            Parameters = new Dictionary<string, string>
            {
                ["batchSize"] = "40",
                ["chunkSize"] = "10",
                ["failEvery"] = "0"
            },
            Tenants = new[] { "brand-a", "brand-b", "brand-c" }
        }, ct);
        await store.UpsertOverrideAsync(new TenantOverride
        {
            JobKey = "order.sync",
            TenantId = "brand-c",
            // brand-c injects a per-item failure to show chunk/step isolation + retry.
            Retry = new RetryPolicy { MaxAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(200) },
            ParameterOverrides = new Dictionary<string, string> { ["failEvery"] = "7" }
        }, ct);

        // --- settlement.close: a saga, one tenant forced into compensation. ---
        await store.UpsertDefinitionAsync(new JobDefinition
        {
            JobKey = "settlement.close",
            Cron = "*/30 * * * * *", // every 30 seconds
            Timeout = TimeSpan.FromSeconds(15),
            Tenants = new[] { "brand-a", "brand-b" }
        }, ct);
        await store.UpsertOverrideAsync(new TenantOverride
        {
            JobKey = "settlement.close",
            TenantId = "brand-b",
            ParameterOverrides = new Dictionary<string, string> { ["failStep"] = "invoice" }
        }, ct);
    }
}

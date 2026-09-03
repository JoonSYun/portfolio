using FlowForge.Abstractions.Configuration;
using FlowForge.Core.Configuration;
using Xunit;

namespace FlowForge.Tests;

public class ConfigResolverTests
{
    private static async Task<ConfigResolver> BuildAsync(
        JobDefinition definition, params TenantOverride[] overrides)
    {
        var store = new InMemoryConfigStore();
        await store.UpsertDefinitionAsync(definition);
        foreach (var o in overrides)
            await store.UpsertOverrideAsync(o);
        return new ConfigResolver(store);
    }

    private static JobDefinition SampleDefinition() => new()
    {
        JobKey = "order.sync",
        Cron = "0 * * * *",
        Timeout = TimeSpan.FromMinutes(5),
        Retry = new RetryPolicy { MaxAttempts = 3 },
        Parameters = new Dictionary<string, string> { ["batchSize"] = "40", ["region"] = "kr" }
    };

    [Fact]
    public async Task Resolve_without_override_uses_domain_defaults()
    {
        var resolver = await BuildAsync(SampleDefinition());

        var config = await resolver.ResolveAsync("order.sync", "brand-a");

        Assert.Equal("0 * * * *", config.Cron);
        Assert.Equal(TimeSpan.FromMinutes(5), config.Timeout);
        Assert.Equal("40", config.Parameters["batchSize"]);
        Assert.True(config.Enabled);
    }

    [Fact]
    public async Task Override_replaces_only_specified_fields()
    {
        var resolver = await BuildAsync(SampleDefinition(), new TenantOverride
        {
            JobKey = "order.sync",
            TenantId = "brand-a",
            Cron = "*/5 * * * *" // only cron differs
        });

        var config = await resolver.ResolveAsync("order.sync", "brand-a");

        Assert.Equal("*/5 * * * *", config.Cron);                 // overridden
        Assert.Equal(TimeSpan.FromMinutes(5), config.Timeout);    // inherited
        Assert.Equal("40", config.Parameters["batchSize"]);       // inherited
    }

    [Fact]
    public async Task Parameter_override_is_a_shallow_merge()
    {
        var resolver = await BuildAsync(SampleDefinition(), new TenantOverride
        {
            JobKey = "order.sync",
            TenantId = "brand-a",
            ParameterOverrides = new Dictionary<string, string> { ["batchSize"] = "100", ["extra"] = "on" }
        });

        var config = await resolver.ResolveAsync("order.sync", "brand-a");

        Assert.Equal("100", config.Parameters["batchSize"]); // overridden
        Assert.Equal("kr", config.Parameters["region"]);     // inherited
        Assert.Equal("on", config.Parameters["extra"]);      // added
    }

    [Fact]
    public async Task ResolveAll_fans_out_to_every_tenant()
    {
        var definition = SampleDefinition() with { Tenants = new[] { "brand-a", "brand-b" } };
        var resolver = await BuildAsync(definition, new TenantOverride
        {
            JobKey = "order.sync",
            TenantId = "brand-b",
            Enabled = false
        });

        var configs = await resolver.ResolveAllAsync("order.sync");

        Assert.Equal(2, configs.Count);
        Assert.True(configs.Single(c => c.TenantId == "brand-a").Enabled);
        Assert.False(configs.Single(c => c.TenantId == "brand-b").Enabled);
    }

    [Fact]
    public async Task Resolve_unknown_job_throws()
    {
        var resolver = await BuildAsync(SampleDefinition());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => resolver.ResolveAsync("nope", "brand-a"));
    }
}

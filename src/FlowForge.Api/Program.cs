using FlowForge.Abstractions.Configuration;
using FlowForge.Abstractions.Operations;
using FlowForge.Api;
using FlowForge.Core.DependencyInjection;
using FlowForge.Core.Jobs;
using FlowForge.Sample;
using FlowForge.Scheduler;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:8080");

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// The whole platform: config resolution, behavior pipeline, in-process bus + stores,
// attribute-bound handlers (discovered from the Sample assembly), and the scheduler.
builder.Services.AddFlowForgeCore(typeof(InventorySnapshotJob).Assembly);
builder.Services.AddFlowForgeScheduler(options => options.TickInterval = TimeSpan.FromSeconds(1));

var app = builder.Build();

// Seed definitions/overrides (a database in production; editable with no deploy).
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<IConfigStore>();
    await SeedData.ApplyAsync(store);
}

// ---- Control plane -----------------------------------------------------------

app.MapGet("/", () => Results.Content(HelpPage.Html, "text/html"));

app.MapGet("/health", (IConfigStore store, JobRegistry registry) => Results.Ok(new
{
    status = "healthy",
    bus = "in-process",
    boundJobs = registry.Handlers.Keys.OrderBy(k => k).ToArray()
}));

// Definitions with their per-tenant effective (merged) configs.
app.MapGet("/jobs", async (IConfigStore store, IConfigResolver resolver) =>
{
    var definitions = await store.GetDefinitionsAsync();
    var view = new List<object>();
    foreach (var def in definitions)
    {
        var effective = await resolver.ResolveAllAsync(def.JobKey);
        view.Add(new
        {
            def.JobKey,
            def.Cron,
            def.Enabled,
            timeoutSeconds = def.Timeout.TotalSeconds,
            tenants = effective.Select(e => new
            {
                e.TenantId,
                e.Cron,
                e.Enabled,
                retryMaxAttempts = e.Retry.MaxAttempts,
                parameters = e.Parameters
            })
        });
    }
    return Results.Ok(view);
});

// Next scheduled occurrences for a job's effective schedules.
app.MapGet("/jobs/{key}/next", async (string key, int? count, IConfigResolver resolver) =>
{
    var take = Math.Clamp(count ?? 3, 1, 20);
    var effective = await resolver.ResolveAllAsync(key);
    var now = DateTimeOffset.UtcNow;
    var result = effective.Select(e =>
    {
        var schedule = CronSchedule.Parse(e.Cron);
        var occurrences = new List<DateTimeOffset>();
        var cursor = now;
        for (var i = 0; i < take; i++)
        {
            var next = schedule.Next(cursor);
            if (next is null) break;
            occurrences.Add(next.Value);
            cursor = next.Value;
        }
        return new { e.TenantId, e.Cron, nextRunsUtc = occurrences };
    });
    return Results.Ok(result);
});

// Manually trigger a job — one tenant (?tenant=) or fan out to all.
app.MapPost("/jobs/{key}/trigger", async (string key, string? tenant, IConfigResolver resolver, IJobDispatcher dispatcher) =>
{
    var now = DateTimeOffset.UtcNow;
    if (tenant is not null)
    {
        var executionId = await dispatcher.DispatchAsync(key, tenant, now);
        return Results.Ok(new { dispatched = 1, executionId });
    }

    var effective = await resolver.ResolveAllAsync(key);
    var ids = new List<string>();
    foreach (var config in effective)
        ids.Add(await dispatcher.DispatchAsync(key, config.TenantId, now));
    return Results.Ok(new { dispatched = ids.Count, executionIds = ids });
});

// Execution history (newest first).
app.MapGet("/executions", async (string? job, string? tenant, int? limit, IExecutionStore store) =>
{
    var records = await store.QueryAsync(job, tenant, Math.Clamp(limit ?? 50, 1, 500));
    return Results.Ok(records);
});

// ---- Three-tier operational control -----------------------------------------

app.MapGet("/control", (IOperationalGate gate) => Results.Ok(gate.Snapshot()));

app.MapPost("/control/pause", (IOperationalGate gate) =>
{
    gate.PauseGlobally();
    return Results.Ok(gate.Snapshot());
});
app.MapPost("/control/resume", (IOperationalGate gate) =>
{
    gate.ResumeGlobally();
    return Results.Ok(gate.Snapshot());
});
app.MapPost("/control/block", (string job, string? tenant, IOperationalGate gate) =>
{
    gate.Block(job, tenant);
    return Results.Ok(gate.Snapshot());
});
app.MapPost("/control/unblock", (string job, string? tenant, IOperationalGate gate) =>
{
    gate.Unblock(job, tenant);
    return Results.Ok(gate.Snapshot());
});
app.MapPost("/control/validation", (bool enabled, IOperationalGate gate) =>
{
    gate.SetValidationMode(enabled);
    return Results.Ok(gate.Snapshot());
});

app.Logger.LogInformation("FlowForge control plane on http://localhost:8080  (open / for the endpoint guide)");

app.Run();

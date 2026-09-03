using FlowForge.Abstractions.Configuration;
using FlowForge.Core.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.Scheduler;

public sealed class SchedulerOptions
{
    /// <summary>How often the scheduler evaluates due schedules.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The scheduling loop. It evaluates each domain's cron on a fixed tick and, when
/// due, fans out to one execution per tenant at fire time. Registration cost stays
/// at "one schedule per domain" no matter how many tenants exist — the fan-out is
/// deferred to the fire step, not paid at registration.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly IConfigStore _configStore;
    private readonly IConfigResolver _resolver;
    private readonly IJobDispatcher _dispatcher;
    private readonly SchedulerOptions _options;
    private readonly ILogger<SchedulerService> _logger;
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new();

    public SchedulerService(
        IConfigStore configStore,
        IConfigResolver resolver,
        IJobDispatcher dispatcher,
        SchedulerOptions options,
        ILogger<SchedulerService> logger)
    {
        _configStore = configStore;
        _resolver = resolver;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler started (tick {Interval}).", _options.TickInterval);
        using var timer = new PeriodicTimer(_options.TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduler tick failed.");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var definition in await _configStore.GetDefinitionsAsync(ct))
        {
            if (!definition.Enabled)
                continue;

            var schedule = CronSchedule.Parse(definition.Cron);

            // First sighting: arm the next occurrence without firing.
            if (!_nextDue.TryGetValue(definition.JobKey, out var due))
            {
                _nextDue[definition.JobKey] = schedule.Next(now) ?? DateTimeOffset.MaxValue;
                continue;
            }

            if (now < due)
                continue;

            await FireAsync(definition, due, ct);
            _nextDue[definition.JobKey] = schedule.Next(now) ?? DateTimeOffset.MaxValue;
        }
    }

    private async Task FireAsync(JobDefinition definition, DateTimeOffset scheduledFor, CancellationToken ct)
    {
        var configs = await _resolver.ResolveAllAsync(definition.JobKey, ct);
        var fired = 0;
        foreach (var config in configs)
        {
            if (!config.Enabled)
                continue;

            await _dispatcher.DispatchAsync(
                definition.JobKey,
                config.TenantId,
                scheduledFor,
                idempotencyKey: $"{definition.JobKey}:{config.TenantId}:{scheduledFor:O}",
                ct);
            fired++;
        }

        _logger.LogInformation("⏰ {Job} due → fanned out to {Count} tenant(s).", definition.JobKey, fired);
    }
}

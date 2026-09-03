using FlowForge.Abstractions.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.Core.Jobs;

/// <summary>
/// At startup, binds every discovered handler to the message bus — one subscription
/// per job key, routed to the shared <see cref="JobRunner"/>. This is the runtime
/// half of the attribute-binding engine: discovery fills the registry, this wires it.
/// </summary>
public sealed class JobBindingService : IHostedService
{
    private readonly IMessageBus _bus;
    private readonly JobRegistry _registry;
    private readonly JobRunner _runner;
    private readonly ILogger<JobBindingService> _logger;

    public JobBindingService(IMessageBus bus, JobRegistry registry, JobRunner runner, ILogger<JobBindingService> logger)
    {
        _bus = bus;
        _registry = registry;
        _runner = runner;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var jobKey in _registry.Handlers.Keys)
        {
            _bus.Subscribe(jobKey, (message, ct) => _runner.RunAsync(message, ct));
            _logger.LogInformation("Bound job '{Job}' → {Handler}.", jobKey, _registry.Handlers[jobKey].Name);
        }
        _logger.LogInformation("FlowForge bound {Count} job handler(s).", _registry.Handlers.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

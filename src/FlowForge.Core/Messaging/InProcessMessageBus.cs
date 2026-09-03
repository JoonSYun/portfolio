using System.Collections.Concurrent;
using System.Threading.Channels;
using FlowForge.Abstractions.Messaging;
using Microsoft.Extensions.Logging;

namespace FlowForge.Core.Messaging;

/// <summary>
/// An in-process message bus: one unbounded channel and one pump task per job key,
/// which models the "separate queue + worker per domain" isolation without any
/// broker. A failing handler is logged and cannot stall other queues. The same
/// <see cref="IMessageBus"/> contract is what a RabbitMQ/MassTransit adapter
/// implements in production (see docs/ARCHITECTURE.md).
/// </summary>
public sealed class InProcessMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly ILogger<InProcessMessageBus> _logger;
    private readonly ConcurrentDictionary<string, Channel<JobMessage>> _queues = new();
    private readonly ConcurrentDictionary<string, byte> _subscribed = new();
    private readonly List<Task> _pumps = new();
    private readonly CancellationTokenSource _cts = new();

    public InProcessMessageBus(ILogger<InProcessMessageBus> logger) => _logger = logger;

    public Task PublishAsync(JobMessage message, CancellationToken ct = default)
    {
        var queue = _queues.GetOrAdd(message.JobKey, _ => Channel.CreateUnbounded<JobMessage>());
        return queue.Writer.WriteAsync(message, ct).AsTask();
    }

    public void Subscribe(string jobKey, Func<JobMessage, CancellationToken, Task> handler)
    {
        if (!_subscribed.TryAdd(jobKey, 0))
            throw new InvalidOperationException($"Job key '{jobKey}' already has a subscriber.");

        var queue = _queues.GetOrAdd(jobKey, _ => Channel.CreateUnbounded<JobMessage>());
        _pumps.Add(Task.Run(() => PumpAsync(jobKey, queue, handler, _cts.Token)));
    }

    private async Task PumpAsync(string jobKey, Channel<JobMessage> queue, Func<JobMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await handler(message, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A poisoned message is isolated to this queue — never fatal to the pump.
                    _logger.LogError(ex, "Handler for '{Job}' failed on message {MessageId}.", jobKey, message.MessageId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var queue in _queues.Values)
            queue.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_pumps);
        }
        catch
        {
            // pumps observe cancellation; nothing to surface on shutdown
        }
        _cts.Dispose();
    }
}

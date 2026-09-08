using MassTransit;
using Microsoft.Extensions.Logging;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    // 1. 큐 관리자 - 큐 제어
    public interface IQueueManager
    {
        Task PauseQueueAsync(string queueName);
        Task ResumeQueueAsync(string queueName);
        void RegisterEndpoint(string queueName, IReceiveEndpoint endpoint);
    }

    public class QueueManager : IQueueManager
    {
        private readonly Dictionary<string, IReceiveEndpoint> _endpoints = new();
        private readonly HashSet<string> _pausedQueues = new();
        private readonly ILogger<QueueManager> _logger;

        public QueueManager(ILogger<QueueManager> logger)
        {
            _logger = logger;
        }

        public void RegisterEndpoint(string queueName, IReceiveEndpoint endpoint)
        {
            _endpoints[queueName] = endpoint;
            _logger.LogInformation($"Queue endpoint registered: {queueName}");
        }

        public async Task PauseQueueAsync(string queueName)
        {
            if (_endpoints.TryGetValue(queueName, out var endpoint))
            {
                await endpoint.Stop();
                _pausedQueues.Add(queueName);
                _logger.LogWarning($"Queue paused: {queueName}");
            }
        }

        public async Task ResumeQueueAsync(string queueName)
        {
            if (_endpoints.TryGetValue(queueName, out var endpoint))
            {
                endpoint.Start();
                _pausedQueues.Remove(queueName);
                _logger.LogInformation($"Queue resumed: {queueName}");
            }
        }
    }
}

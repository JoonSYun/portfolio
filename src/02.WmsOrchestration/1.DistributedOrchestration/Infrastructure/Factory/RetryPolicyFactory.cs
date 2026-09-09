using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    public interface IRetryPolicyFactory
    {
        AsyncRetryPolicy CreateRetryPolicy();
    }

    public abstract class RetryPolicyFactoryBase : IRetryPolicyFactory
    {
        protected readonly ILogger Logger;
        protected readonly IOptionsMonitor<RetryPolicyOptions> Options;

        protected RetryPolicyFactoryBase(
            ILogger logger,
            IOptionsMonitor<RetryPolicyOptions> options)
        {
            Logger = logger;
            Options = options;
        }

        public abstract AsyncRetryPolicy CreateRetryPolicy();

        protected AsyncRetryPolicy CreatePolicy(PolicySettings config, string operationName)
        {
            return Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    retryCount: config.RetryCount,
                    sleepDurationProvider: retryAttempt =>
                        TimeSpan.FromSeconds(config.InitialDelaySeconds * Math.Pow(2, retryAttempt - 1)),
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        Logger.LogWarning(exception,
                            "{OperationName} 작업 재시도 {RetryCount}/{MaxRetry}차 - {Delay}초 후 재시도",
                            operationName, retryCount, config.RetryCount, timeSpan.TotalSeconds);
                    });
        }
    }

    public class OrchestrationStatusRetryPolicyFactory : RetryPolicyFactoryBase
    {
        public OrchestrationStatusRetryPolicyFactory(
            ILogger<OrchestrationStatusRetryPolicyFactory> logger,
            IOptionsMonitor<RetryPolicyOptions> options)
            : base(logger, options)
        {
        }

        public override AsyncRetryPolicy CreateRetryPolicy()
        {
            var config = Options.CurrentValue.OrchestrationStatus;
            return CreatePolicy(config, "OrchestrationStatus");
        }
    }

    public class OrchestrationRethriveRetryPolicyFactory : RetryPolicyFactoryBase
    {
        public OrchestrationRethriveRetryPolicyFactory(
            ILogger<OrchestrationRethriveRetryPolicyFactory> logger,
            IOptionsMonitor<RetryPolicyOptions> options)
            : base(logger, options)
        {
        }

        public override AsyncRetryPolicy CreateRetryPolicy()
        {
            var config = Options.CurrentValue.OrchestrationRethrive;
            return CreatePolicy(config, "OrchestrationRethrive");
        }
    }

    public class HTTPGetRetryPolicyFactory : RetryPolicyFactoryBase
    {
        public HTTPGetRetryPolicyFactory(
            ILogger<HTTPGetRetryPolicyFactory> logger,
            IOptionsMonitor<RetryPolicyOptions> options)
            : base(logger, options)
        {
        }
        public override AsyncRetryPolicy CreateRetryPolicy()
        {
            var config = Options.CurrentValue.HTTPGet;
            return CreatePolicy(config, "HTTPGet");
        }
    }

    public class RedisRetryPolicyFactory : RetryPolicyFactoryBase
    {
        public RedisRetryPolicyFactory(
            ILogger<RedisRetryPolicyFactory> logger,
            IOptionsMonitor<RetryPolicyOptions> options)
            : base(logger, options)
        {
        }

        public override AsyncRetryPolicy CreateRetryPolicy()
        {
            var config = Options.CurrentValue.Redis;
            return CreatePolicy(config, "Redis");
        }
    }
}
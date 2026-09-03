using System.Reflection;
using FlowForge.Abstractions.Configuration;
using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Messaging;
using FlowForge.Abstractions.Operations;
using FlowForge.Abstractions.Resilience;
using FlowForge.Core.Configuration;
using FlowForge.Core.Jobs;
using FlowForge.Core.Messaging;
using FlowForge.Core.Operations;
using FlowForge.Core.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowForge.Core.DependencyInjection;

public static class FlowForgeCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the FlowForge core: config resolution, the behavior pipeline, the
    /// in-process bus and stores, and the attribute-binding engine. Handlers in
    /// <paramref name="handlerAssemblies"/> are discovered and wired automatically.
    /// Swap any store/bus registration for a production adapter after this call.
    /// </summary>
    public static IServiceCollection AddFlowForgeCore(
        this IServiceCollection services,
        params Assembly[] handlerAssemblies)
    {
        // Stores and infrastructure (in-memory defaults; TryAdd lets callers override).
        services.TryAddSingleton<IConfigStore, InMemoryConfigStore>();
        services.TryAddSingleton<IConfigResolver, ConfigResolver>();
        services.TryAddSingleton<IExecutionStore, InMemoryExecutionStore>();
        services.TryAddSingleton<IOperationalGate, InMemoryOperationalGate>();
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>();

        services.TryAddSingleton<InProcessMessageBus>();
        services.TryAddSingleton<IMessageBus>(sp => sp.GetRequiredService<InProcessMessageBus>());

        // Cross-cutting behavior pipeline (ordered by IJobBehavior.Order at runtime).
        services.AddSingleton<IJobBehavior, LoggingBehavior>();
        services.AddSingleton<IJobBehavior, ExecutionHistoryBehavior>();
        services.AddSingleton<IJobBehavior, OperationalGateBehavior>();
        services.AddSingleton<IJobBehavior, IdempotencyBehavior>();
        services.AddSingleton<IJobBehavior, RetryBehavior>();
        services.AddSingleton<IJobBehavior, TimeoutBehavior>();

        services.TryAddSingleton<IJobDispatcher, JobDispatcher>();
        services.TryAddSingleton<JobRunner>();

        // Attribute-binding engine: discover [JobHandler] types and register them.
        var registry = new JobRegistry();
        var assemblies = handlerAssemblies.Length > 0
            ? handlerAssemblies
            : new[] { Assembly.GetCallingAssembly() };

        foreach (var (jobKey, handlerType) in HandlerBinder.Discover(assemblies))
        {
            services.AddScoped(handlerType);
            registry.Register(jobKey, handlerType);
        }

        services.AddSingleton(registry);
        services.AddHostedService<JobBindingService>();

        return services;
    }
}

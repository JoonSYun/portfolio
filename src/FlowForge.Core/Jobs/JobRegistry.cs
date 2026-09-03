using System.Reflection;
using FlowForge.Abstractions.Jobs;
using FlowForge.Abstractions.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace FlowForge.Core.Jobs;

/// <summary>
/// The job-key → handler-type map built by the binding engine. Resolving a handler
/// goes through DI so it gets a fresh, fully-injected instance per execution scope.
/// </summary>
public sealed class JobRegistry
{
    private readonly Dictionary<string, Type> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Type> Handlers => _handlers;

    public void Register(string jobKey, Type handlerType)
    {
        if (_handlers.TryGetValue(jobKey, out var existing) && existing != handlerType)
            throw new InvalidOperationException(
                $"Job key '{jobKey}' is already bound to {existing.Name}; cannot also bind {handlerType.Name}.");
        _handlers[jobKey] = handlerType;
    }

    public IJob Resolve(IServiceProvider provider, string jobKey)
    {
        if (!_handlers.TryGetValue(jobKey, out var type))
            throw new InvalidOperationException($"No handler bound for job key '{jobKey}'.");
        return (IJob)provider.GetRequiredService(type);
    }
}

/// <summary>
/// Discovers <c>[JobHandler]</c>-decorated types by reflection. This is the
/// attribute-binding engine: a new domain becomes routable simply by adding the
/// attribute, with no central registration list to edit.
/// </summary>
public static class HandlerBinder
{
    public static IReadOnlyList<(string JobKey, Type HandlerType)> Discover(params Assembly[] assemblies)
    {
        var result = new List<(string, Type)>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attribute = type.GetCustomAttribute<JobHandlerAttribute>();
                if (attribute is null)
                    continue;

                if (type.IsAbstract || !typeof(IJob).IsAssignableFrom(type))
                    throw new InvalidOperationException(
                        $"{type.FullName} is marked [JobHandler] but is not a concrete IJob implementation.");

                result.Add((attribute.JobKey, type));
            }
        }
        return result;
    }
}

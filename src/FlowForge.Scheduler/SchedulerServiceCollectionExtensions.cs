using Microsoft.Extensions.DependencyInjection;

namespace FlowForge.Scheduler;

public static class SchedulerServiceCollectionExtensions
{
    /// <summary>Adds the cron scheduling loop that fans domains out to tenant executions.</summary>
    public static IServiceCollection AddFlowForgeScheduler(
        this IServiceCollection services,
        Action<SchedulerOptions>? configure = null)
    {
        var options = new SchedulerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddHostedService<SchedulerService>();
        return services;
    }
}

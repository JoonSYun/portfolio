using Hangfire;
using Portfolio.UnifiedScheduler.ExecutionPipeline.Jobs;

namespace Portfolio.UnifiedScheduler.ExecutionPipeline;

/// <summary>
/// 큐에서 꺼낸 <see cref="JobRequest"/> 를 도메인 코드에 맞는 파생 Job 으로 라우팅한다.
/// 파생 Job 은 DI 스코프에서 해석되므로, 새 도메인 추가 = Job 클래스 + 아래 한 줄.
/// (오케스트레이션 플랫폼에서는 이 등록마저 Attribute 로 자동화했다 → 05.WmsOrchestration/2.BaseFramework)
/// </summary>
public static class JobDispatcher
{
    private static readonly IReadOnlyDictionary<string, Type> Registry = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["RETURN_PICKUP"]   = typeof(ReturnPickupInstructionJob),
        ["ORDER_SYNC"]      = typeof(OrderSyncJob),
        ["STOCK_CLOSE"]     = typeof(StockClosingJob),
        // ... 파생 Job 69종
    };

    public static IServiceProvider Services { get; set; } = default!;

    [Queue("{0}")] // 큐 이름은 요청에서 결정 — 도메인마다 워커 서버가 다르다
    public static async Task ExecuteAsync(JobRequest request, CancellationToken ct)
    {
        if (!Registry.TryGetValue(request.Schedule.DomainCode, out var jobType))
            throw new InvalidOperationException($"등록되지 않은 도메인: {request.Schedule.DomainCode}");

        await using var scope = Services.CreateAsyncScope();
        var job = (JobBase)scope.ServiceProvider.GetRequiredService(jobType);
        await job.RunAsync(request, ct);
    }
}

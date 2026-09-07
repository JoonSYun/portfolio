using Hangfire;
using Portfolio.UnifiedScheduler.ExecutionPipeline;

namespace Portfolio.UnifiedScheduler.FaultIsolationAndOperations;

/// <summary>
/// [담당업무 3] 8단계 Notify — 알림 전용 워커.
/// <see cref="JobBase"/> 는 결과를 notify 큐에 넣고 바로 끝난다.
/// 메신저/메일 장애가 실행 워커의 처리량이나 이력 확정에 영향을 주지 않는다.
/// </summary>
public sealed class NotificationWorker : IJobNotifier
{
    public const string QueueName = "notify";

    private readonly IBackgroundJobClient _jobs;
    public NotificationWorker(IBackgroundJobClient jobs) => _jobs = jobs;

    public Task EnqueueAsync(JobOutcome outcome)
    {
        _jobs.Enqueue(QueueName, () => SendAsync(outcome));
        return Task.CompletedTask;
    }

    [Queue(QueueName)]
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 10, 30, 60, 300, 600 })]
    public static async Task SendAsync(JobOutcome outcome)
    {
        // 실패·타임아웃만 알린다. 성공은 콘솔 이력에서 본다 (알림 피로 방지).
        if (outcome.State is not (JobExecutionState.Failed or JobExecutionState.TimedOut)) return;

        var text = $"[{outcome.DomainCode}/{outcome.BrandCode}] {outcome.State} — {outcome.Message} ({outcome.Elapsed.TotalSeconds:0.0}s)";
        await MessengerClient.PostAsync(channel: "#scheduler-alerts", text);
    }
}

internal static class MessengerClient
{
    public static Task PostAsync(string channel, string text) => Task.CompletedTask; // 사내 메신저 Webhook
}

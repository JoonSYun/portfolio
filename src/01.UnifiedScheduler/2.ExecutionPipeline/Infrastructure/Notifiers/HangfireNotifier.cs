// [담당업무 3] 알림 격리 — INotifier 어댑터. 실행 워커는 알림 큐에 enqueue 만 하고 즉시 리턴한다.

using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Options;
using Portfolio.UnifiedScheduler.Jobs;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// <see cref="INotifier"/> 의 Hangfire 어댑터. 호출 즉시 <see cref="Jobs.NotificationJob"/> 을 알림 전용 큐로
/// enqueue 하고 리턴 — 호출자(도메인 잡 워커)는 외부 채널 응답을 절대 기다리지 않는다.
/// </summary>
/// <remarks>
/// <see cref="Send"/> 는 동기 시그니처를 유지(기존 호출부 변경 없음)하지만 내부 동작은 fire-and-forget enqueue.
/// 실제 SMTP/메신저 호출은 알림 큐의 별도 워커가 <see cref="Jobs.NotificationJob.SendAsync"/> 안에서 timeout 적용해 처리.
/// <para/>
/// IdempotencyFilter 가 검사하지 않음 — <see cref="NotificationJob.SendAsync"/> 시그니처가
/// <see cref="Models.JobArgs"/> 를 받지 않아 자연스럽게 우회된다.
/// </remarks>
public class HangfireNotifier : INotifier
{
    private readonly IBackgroundJobClient _bg;
    private readonly string               _queue;

    public HangfireNotifier(IBackgroundJobClient bg, IOptions<HangfireOptions> options)
    {
        _bg    = bg;
        _queue = options.Value.Notifier.QueueName;
    }

    public void Send(NotificationRequest request)
    {
        // CancellationToken.None 은 enqueue 직렬화용 placeholder — Hangfire 1.8 이 worker 시점에
        // ServerJobCancellationToken 으로 자동 교체한다 (Dispatcher 의 EnqueueTarget 와 동일 패턴).
        // NotificationRequest 는 parameterless ctor + 단순 get/set 이라 Hangfire JSON 직렬화로 안전하게 라운드트립된다.
        var job = Job.FromExpression<NotificationJob>(
            j => j.SendAsync(request, CancellationToken.None));

        _bg.Create(job, new EnqueuedState(_queue));
    }
}

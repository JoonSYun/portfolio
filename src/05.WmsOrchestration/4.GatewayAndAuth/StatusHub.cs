using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.GatewayAndAuth;

/// <summary>
/// [담당업무 4] 실시간 상태 모니터링 — SignalR 허브 (발췌).
/// 사용자별 알림 라우팅(개인 · 관리자 · 브랜드 그룹 fan-out)은
/// <c>1.DistributedOrchestration/Service/NotificationService.cs</c> 와 <c>Infrastructure/SignalR/OrchestrationHub.cs</c> 가 담당하고,
/// 이 파일은 Saga 가 발행하는 이벤트를 운영자 채널로 그대로 중계하는 최소 예시다.
/// 운영자는 새로고침 없이 "Job 이 몇 번째 청크까지 왔는지, 어디서 보상이 시작됐는지" 를 본다.
/// </summary>
[Authorize(Policy = "operator")]
public sealed class StatusHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "operators");
        var tenant = Context.User?.FindFirst("tenant")?.Value;
        if (tenant is not null) await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenant}");   // 테넌트별 채널
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Saga 가 발행하는 이벤트를 허브로 중계.
///   · ChunkResultEvent           — ChunkStepSaga 가 청크 하나를 마감할 때 (COMPLETED / PARTIAL_SUCCESS / FAILED)
///   · JobCompleteUpdateCommand   — JobSaga 가 Job 최종 상태를 확정할 때
///   · CompensationFailAlertEvent — 보상 Saga 가 실패해 운영자 개입이 필요할 때
/// </summary>
public sealed class JobStatusRelay :
    IConsumer<ChunkResultEvent>,
    IConsumer<JobCompleteUpdateCommand>,
    IConsumer<CompensationFailAlertEvent>
{
    private readonly IHubContext<StatusHub> _hub;
    public JobStatusRelay(IHubContext<StatusHub> hub) => _hub = hub;

    public Task Consume(ConsumeContext<ChunkResultEvent> ctx) =>
        _hub.Clients.Group("operators").SendAsync("chunk", new
        {
            ctx.Message.JobId, ctx.Message.ChunkId, ctx.Message.ChunkIndex,
            ctx.Message.Status, ctx.Message.ProcessedItemCount
        });

    public Task Consume(ConsumeContext<JobCompleteUpdateCommand> ctx) =>
        _hub.Clients.Group("operators").SendAsync("job", new
        {
            ctx.Message.JobId, state = ctx.Message.Status,
            ctx.Message.ChunkSuccessCount, ctx.Message.ChunkPartialSuccessCount, ctx.Message.ChunkFailedCount
        });

    public Task Consume(ConsumeContext<CompensationFailAlertEvent> ctx) =>
        _hub.Clients.Group("operators").SendAsync("compensation-failed", new
        {
            ctx.Message.JobId, ctx.Message.ChunkId, ctx.Message.CompensationId,
            ctx.Message.FailedStepIndex, ctx.Message.FailedStepType, ctx.Message.ErrorMessage
        });
}

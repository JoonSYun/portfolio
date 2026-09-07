using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Portfolio.WmsOrchestration.DistributedOrchestration;

namespace Portfolio.WmsOrchestration.GatewayAndAuth;

/// <summary>
/// [담당업무 4] 실시간 상태 모니터링 — SignalR 허브.
/// 컨슈머(ConsumerBase.NotifyStatusAsync)와 Saga 이벤트가 운영 화면으로 push 된다.
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

/// <summary>Saga 의 Job 수준 이벤트를 허브로 중계.</summary>
public sealed class JobStatusRelay : IConsumer<BulkSyncJobCompleted>, IConsumer<BulkSyncJobCompensated>, IConsumer<ChunkCompleted>
{
    private readonly IHubContext<StatusHub> _hub;
    public JobStatusRelay(IHubContext<StatusHub> hub) => _hub = hub;

    public Task Consume(ConsumeContext<ChunkCompleted> ctx) =>
        _hub.Clients.Group("operators").SendAsync("chunk", new { ctx.Message.JobId, ctx.Message.ChunkId, ctx.Message.Succeeded, ctx.Message.Failed });

    public Task Consume(ConsumeContext<BulkSyncJobCompleted> ctx) =>
        _hub.Clients.Group("operators").SendAsync("job", new { ctx.Message.JobId, state = "Completed", ctx.Message.Succeeded, ctx.Message.Failed });

    public Task Consume(ConsumeContext<BulkSyncJobCompensated> ctx) =>
        _hub.Clients.Group("operators").SendAsync("job", new { ctx.Message.JobId, state = "Compensated", ctx.Message.Reason });
}

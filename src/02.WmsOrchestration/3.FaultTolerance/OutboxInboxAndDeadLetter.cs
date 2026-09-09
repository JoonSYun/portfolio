using MassTransit;
using Microsoft.EntityFrameworkCore;
using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.FaultTolerance;

/// <summary>
/// [담당업무 3] Outbox / Inbox — 메시지 유실과 중복을 DB 트랜잭션에 묶어 막는다.
///
/// Outbox : 컨슈머가 상태를 바꾸고 다음 메시지를 발행할 때, 발행을 같은 DB 트랜잭션의 outbox 행으로 남긴다.
///          트랜잭션이 커밋되면 별도 배달자가 브로커로 보낸다. "DB 는 바뀌었는데 메시지는 안 나감" 이 불가능.
/// Inbox  : 수신한 MessageId 를 같은 트랜잭션에 기록한다. 브로커가 재전달해도 두 번 반영되지 않는다.
///          (Redis 이중 멱등성은 빠른 1차 방어, Inbox 는 트랜잭션 수준의 최종 방어)
/// MassTransit 의 EF Core Outbox 를 쓴다 — Saga 상태(Job/JobProcess/ChunkStep/Compensation 4종) + Outbox + Inbox 가
/// 한 DbContext 에 있다. 각 Saga 의 EF 매핑은 <c>1.DistributedOrchestration/Saga/State/*StateMap.cs</c> 참조.
/// </summary>
public sealed class OrchestrationDbContext : SagaDbContext
{
    public OrchestrationDbContext(DbContextOptions<OrchestrationDbContext> o) : base(o) { }

    protected override IEnumerable<ISagaClassMap> Configurations
    {
        get
        {
            yield return new JobSagaStateMap();
            yield return new JobProcessSagaStateMap();
            yield return new ChunkStepSagaStateMap();
            yield return new ChunkStepCompensationSagaStateMap();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.AddInboxStateEntity();          // inbox_state
        b.AddOutboxMessageEntity();       // outbox_message
        b.AddOutboxStateEntity();         // outbox_state
    }
}

/// <summary>
/// [담당업무 3] DLQ 자동 재처리 — 처리하지 못한 메시지는 _error 큐에 모인다.
/// 재처리 가능한 원인(일시 장애)이면 원 큐로 되돌리고, 아니면 운영자 큐로 보내 알린다.
/// Hangfire 로 10분마다 실행. "재처리 가능 여부" 는 fault 메시지의 예외 타입으로 판단한다.
/// </summary>
public sealed class DeadLetterReprocessor
{
    private readonly IBus _bus;
    private readonly IDeadLetterReader _dlq;
    private readonly ILogger<DeadLetterReprocessor> _log;

    public DeadLetterReprocessor(IBus bus, IDeadLetterReader dlq, ILogger<DeadLetterReprocessor> log) { _bus = bus; _dlq = dlq; _log = log; }

    private static readonly HashSet<string> Transient = new(StringComparer.OrdinalIgnoreCase)
    { "TimeoutException", "SocketException", "HttpRequestException", "NpgsqlException", "SqlException", "TransientStepException", "BrokerUnreachableException" };

    public async Task RunAsync(CancellationToken ct)
    {
        var requeued = 0; var escalated = 0;
        await foreach (var fault in _dlq.ReadAsync(max: 200, ct))
        {
            var lastException = fault.Exceptions.LastOrDefault()?.ExceptionType.Split('.').Last() ?? "";
            if (Transient.Contains(lastException) && fault.RetryCount < 5)
            {
                await _bus.GetSendEndpoint(fault.OriginalQueue).ContinueWith(t => t.Result.Send(fault.Message, ctx => ctx.Headers.Set("MT-Redelivery-Count", fault.RetryCount + 1)), ct);
                requeued++;
            }
            else
            {
                await _bus.Publish(new OperatorAttentionRequired(fault.MessageId, fault.OriginalQueue.ToString(), lastException, fault.Exceptions.LastOrDefault()?.Message), ct);
                escalated++;
            }
            await _dlq.AcknowledgeAsync(fault, ct);
        }
        if (requeued + escalated > 0) _log.LogInformation("DLQ 재처리 {Requeued} / 운영자 이관 {Escalated}", requeued, escalated);
    }
}

public record OperatorAttentionRequired(Guid? MessageId, string Queue, string ExceptionType, string? Message);
public record FaultedMessage(Guid? MessageId, Uri OriginalQueue, object Message, IReadOnlyList<ExceptionInfo> Exceptions, int RetryCount);
public interface IDeadLetterReader
{
    IAsyncEnumerable<FaultedMessage> ReadAsync(int max, CancellationToken ct);
    Task AcknowledgeAsync(FaultedMessage m, CancellationToken ct);
}

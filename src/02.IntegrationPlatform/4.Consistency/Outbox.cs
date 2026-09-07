using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Portfolio.IntegrationPlatform.ResultModel;
using Portfolio.IntegrationPlatform.RuleConfiguration;

namespace Portfolio.IntegrationPlatform.Consistency;

/// <summary>Outbox 행 — 상태 변경과 같은 트랜잭션으로 저장되는 "발행 의도".</summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public required string Topic { get; set; }          // integration.run.completed / integration.item.retry
    public required string PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public interface IOutboxWriter { Task StageAsync(IntegrationRunResult run, CancellationToken ct); }

/// <summary>
/// [담당업무 4] Outbox 패턴 — 전송 보장.
///
/// 실행 이력(integ.Run)과 후속 이벤트(Outbox)를 **같은 SaveChanges(=같은 트랜잭션)** 로 저장한다.
/// 이력은 남았는데 알림/후속 처리가 유실되는 경우, 또는 그 반대가 구조적으로 불가능해진다.
/// 실패 건은 재전송 토픽으로 함께 스테이징되어, 외부 반영 실패가 "미전송" 으로 관리된다.
/// </summary>
public sealed class OutboxWriter : IOutboxWriter
{
    private readonly IntegrationDbContext _db;
    public OutboxWriter(IntegrationDbContext db) => _db = db;

    public async Task StageAsync(IntegrationRunResult run, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        _db.Runs.Add(new IntegrationRunRecord
        {
            CorrelationId = run.CorrelationId, ConnectorCode = run.ConnectorCode, BrandCode = run.BrandCode,
            TotalCount = run.TotalCount, SuccessCount = run.SuccessCount, ElapsedMs = (int)run.Elapsed.TotalMilliseconds,
            ItemsJson = JsonSerializer.Serialize(run.Items.Select(i => new { i.ItemKey, i.IsSuccess, i.Verdicts })),
            CreatedAt = now
        });

        _db.Outbox.Add(new OutboxMessage
        {
            Topic = "integration.run.completed",
            PayloadJson = JsonSerializer.Serialize(new { run.CorrelationId, run.ConnectorCode, run.BrandCode, run.SuccessCount, run.FailureCount, Notification = FailureNotificationFormatter.Format(run) }),
            CreatedAt = now
        });

        // 외부 반영 단계에서 실패한 건만 재전송 후보 — WMS 는 이미 확정됐으므로 외부만 다시 보내면 된다
        foreach (var item in run.Failures.Where(f => f.FirstFailure!.Stage == Stage.ExternalCommit))
        {
            _db.Outbox.Add(new OutboxMessage
            {
                Topic = "integration.item.retry",
                PayloadJson = JsonSerializer.Serialize(new { run.CorrelationId, run.ConnectorCode, run.BrandCode, item.ItemKey, item.FirstFailure!.RemoteCode }),
                CreatedAt = now
            });
        }

        await _db.SaveChangesAsync(ct);   // ← 이력 + 알림 + 재전송 의도가 원자적으로 커밋된다
    }
}

/// <summary>미발행 Outbox 행을 주기적으로 배출한다. 발행 실패는 Attempts/LastError 로 남기고 다음 주기에 재시도.</summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOutboxPublisher _publisher;
    private readonly ILogger<OutboxDispatcher> _log;

    public OutboxDispatcher(IServiceScopeFactory scopes, IOutboxPublisher publisher, ILogger<OutboxDispatcher> log)
    { _scopes = scopes; _publisher = publisher; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

            var pending = await db.Outbox.Where(m => m.PublishedAt == null && m.Attempts < 10)
                .OrderBy(m => m.Id).Take(100).ToListAsync(ct);

            foreach (var m in pending)
            {
                try { await _publisher.PublishAsync(m.Topic, m.PayloadJson, ct); m.PublishedAt = DateTime.UtcNow; }
                catch (Exception ex) { m.Attempts++; m.LastError = ex.Message; _log.LogWarning(ex, "Outbox 발행 실패 #{Id}", m.Id); }
            }
            await db.SaveChangesAsync(ct);
        }
    }
}

public interface IOutboxPublisher { Task PublishAsync(string topic, string payloadJson, CancellationToken ct); }

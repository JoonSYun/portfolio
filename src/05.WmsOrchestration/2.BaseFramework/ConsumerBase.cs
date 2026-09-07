using System.Diagnostics;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Portfolio.WmsOrchestration.FaultTolerance;
using Portfolio.WmsOrchestration.GatewayAndAuth;

namespace Portfolio.WmsOrchestration.BaseFramework;

/// <summary>컨슈머 공통 의존성 묶음 — 파생 컨슈머 생성자를 한 파라미터로 유지한다.</summary>
public sealed record ConsumerDependencies(ILoggerFactory Loggers, IIdempotencyStore Idempotency, IHubContext<StatusHub> Status);

/// <summary>
/// [담당업무 2] 확장을 위한 Base Framework — 로깅·중복 방지·상태 알림을 상위 클래스로.
///
/// 모든 메시지 컨슈머가 필요로 하는 세 가지를 여기서 한 번만 한다:
///   · 구조적 로깅 (메시지ID·상관관계ID·큐·소요시간)
///   · 중복 방지 — 이중 멱등성 체크 (3.FaultTolerance/DoubleIdempotency)
///   · 실시간 상태 알림 — SignalR 허브로 진행 상황 push (4.GatewayAndAuth/StatusHub)
///
/// 파생 컨슈머는 <see cref="HandleAsync"/> 와 <see cref="DedupKey"/> 만 쓴다.
/// 큐 등록은 <see cref="MessageQueueAttribute"/> 하나로 끝난다 → 신규 도메인 = 비즈니스 로직만.
/// </summary>
public abstract class ConsumerBase<TMessage> : IConsumer<TMessage> where TMessage : class
{
    protected ILogger Logger { get; }
    private readonly IIdempotencyStore _idempotency;
    private readonly IHubContext<StatusHub> _status;

    protected ConsumerBase(ConsumerDependencies deps)
    {
        Logger = deps.Loggers.CreateLogger(GetType());
        _idempotency = deps.Idempotency;
        _status = deps.Status;
    }

    /// <summary>비즈니스 중복 키 — 같은 키의 메시지는 MessageId 가 달라도 한 번만 처리된다.</summary>
    protected abstract string DedupKey(TMessage message);

    /// <summary>고유 로직.</summary>
    protected abstract Task HandleAsync(ConsumeContext<TMessage> context);

    public async Task Consume(ConsumeContext<TMessage> ctx)
    {
        var sw = Stopwatch.StartNew();
        using var scope = Logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = ctx.MessageId, ["CorrelationId"] = ctx.CorrelationId,
            ["Queue"] = ctx.ReceiveContext.InputAddress.AbsolutePath, ["Consumer"] = GetType().Name
        });

        // 이중 멱등성: ① MessageId (전송 계층 중복)  ② 비즈니스 키 (재발행·재시도 중복)
        var claim = await _idempotency.TryClaimAsync(ctx.MessageId?.ToString() ?? "", DedupKey(ctx.Message), ctx.CancellationToken);
        if (!claim.Acquired)
        {
            Logger.LogInformation("중복 메시지 스킵 ({Reason})", claim.Reason);
            return;
        }

        try
        {
            await HandleAsync(ctx);
            await claim.CompleteAsync();
            Logger.LogInformation("처리 완료 {Elapsed}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            await claim.ReleaseAsync();   // 실패는 재시도 가능해야 한다 — 클레임을 푼다
            Logger.LogError(ex, "처리 실패 {Elapsed}ms", sw.ElapsedMilliseconds);
            throw;                        // MassTransit 재시도/재전달/DLQ 정책으로 넘긴다
        }
    }

    /// <summary>운영 화면으로 진행 상황을 실시간 push.</summary>
    protected Task NotifyStatusAsync(string message) =>
        _status.Clients.Group("operators").SendAsync("status", new { consumer = GetType().Name, message, at = DateTime.UtcNow });
}

namespace Portfolio.IntegrationPlatform.Consistency;

/// <summary>
/// [담당업무 4] 시스템 간 정합성 — 처리 순서를 "WMS 우선" 으로 고정.
///
/// 자사 WMS 와 외부 시스템은 하나의 트랜잭션으로 묶을 수 없다. 둘 중 하나는 먼저 커밋되고,
/// 그 사이에 실패하면 불일치가 생긴다. 어느 쪽이 먼저 확정되어야 안전한가?
///
///   외부 먼저 → WMS 실패 : 상대는 "접수됨" 인데 우리 창고엔 지시가 없다. 출고 누락. 되돌리기 어렵다.
///   WMS 먼저  → 외부 실패 : 우리 창고엔 지시가 있고 상대만 모른다. 재전송하면 된다. 되돌리기 쉽다.
///
/// 그래서 WMS 를 먼저 확정하고, 외부 반영 실패는 재시도 가능한 상태(Outbox)로 남긴다.
/// 이 순서는 커넥터가 바꿀 수 없도록 정책 객체로 강제한다.
/// </summary>
public sealed class WmsFirstOrderingPolicy
{
    private readonly ILogger<WmsFirstOrderingPolicy> _log;
    public WmsFirstOrderingPolicy(ILogger<WmsFirstOrderingPolicy> log) => _log = log;

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> wmsAction,
        Func<CancellationToken, Task> externalAction,
        Func<Exception, CancellationToken, Task> onExternalFailure,
        CancellationToken ct)
    {
        // 1. 자사 WMS 먼저 — 여기서 실패하면 아무것도 바깥으로 나가지 않았다. 깨끗한 실패.
        await wmsAction(ct);

        // 2. 외부 반영 — 실패해도 WMS 는 이미 확정. 불일치가 아니라 "미전송" 상태다.
        try
        {
            await externalAction(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "외부 반영 실패 — WMS 는 확정됨. 재전송 대상으로 남김.");
            await onExternalFailure(ex, ct);   // ItemResult 에 실패 단계 기록 → Outbox 재전송 후보
        }
    }
}

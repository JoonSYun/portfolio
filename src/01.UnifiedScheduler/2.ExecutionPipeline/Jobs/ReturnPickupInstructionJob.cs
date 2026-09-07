using Portfolio.UnifiedScheduler.ConfigurationInheritance;
using Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.History;

namespace Portfolio.UnifiedScheduler.ExecutionPipeline.Jobs;

/// <summary>
/// 파생 Job 예시 — 반품 회수 지시 자동화 (ERP 연동 프로젝트에서 스케줄러 도메인으로 등록한 그 Job).
///
/// 보이는 그대로다: 검증 하나, 비즈니스 로직 하나. 로깅·재시도·타임아웃·이력·알림은 <see cref="JobBase"/> 가 한다.
/// 파생 Job 69종이 전부 이 크기다.
/// </summary>
public sealed class ReturnPickupInstructionJob : JobBase
{
    private readonly IReturnPickupService _pickup;

    public ReturnPickupInstructionJob(ILogger<ReturnPickupInstructionJob> log, IExecutionHistoryWriter history,
        IJobNotifier notifier, IReturnPickupService pickup) : base(log, history, notifier) => _pickup = pickup;

    protected override Task<ValidationFailure?> ValidateAsync(EffectiveSchedule s) =>
        Task.FromResult(s.Parameters.ContainsKey("centerCode") ? null : new ValidationFailure("centerCode 파라미터 누락"));

    protected override async Task<CoreResult> ExecuteCoreAsync(EffectiveSchedule s, CancellationToken ct)
    {
        var center = s.Parameters["centerCode"];
        var pending = await _pickup.FindPendingReturnsAsync(s.BrandCode, center, ct);
        var issued = await _pickup.IssuePickupInstructionsAsync(pending, ct);
        return new CoreResult(issued, $"{s.BrandCode}/{center} 회수 지시 {issued}건");
    }
}

public interface IReturnPickupService
{
    Task<IReadOnlyList<Guid>> FindPendingReturnsAsync(string brand, string center, CancellationToken ct);
    Task<int> IssuePickupInstructionsAsync(IReadOnlyList<Guid> returnIds, CancellationToken ct);
}

/// <summary>파생 Job 예시 2 — 온라인 주문 동기화.</summary>
public sealed class OrderSyncJob : JobBase
{
    public OrderSyncJob(ILogger<OrderSyncJob> log, IExecutionHistoryWriter h, IJobNotifier n) : base(log, h, n) { }
    protected override Task<CoreResult> ExecuteCoreAsync(EffectiveSchedule s, CancellationToken ct) =>
        Task.FromResult(new CoreResult(0, "…주문 동기화 로직…"));
}

/// <summary>파생 Job 예시 3 — 재고 마감.</summary>
public sealed class StockClosingJob : JobBase
{
    public StockClosingJob(ILogger<StockClosingJob> log, IExecutionHistoryWriter h, IJobNotifier n) : base(log, h, n) { }
    protected override Task<CoreResult> ExecuteCoreAsync(EffectiveSchedule s, CancellationToken ct) =>
        Task.FromResult(new CoreResult(0, "…재고 마감 로직…"));
}

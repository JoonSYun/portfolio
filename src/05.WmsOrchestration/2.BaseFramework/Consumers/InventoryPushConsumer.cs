using MassTransit;

namespace Portfolio.WmsOrchestration.BaseFramework.Consumers;

public record InventoryChanged(string TenantId, string Sku, int Available, DateTime ChangedAt);

/// <summary>
/// 신규 도메인 예시 — 재고 변경을 외부 쇼핑몰에 push.
/// 이 파일이 전부다: Attribute 한 줄, 중복 키 한 줄, 비즈니스 로직 하나.
/// 큐 선언·바인딩·재시도·DLQ·멱등성·로깅·상태 알림은 프레임워크가 한다.
/// </summary>
[MessageQueue("wms.inventory.push", PrefetchCount = 32, ConcurrentMessageLimit = 16)]
public sealed class InventoryPushConsumer : ConsumerBase<InventoryChanged>
{
    private readonly IMallInventoryApi _mall;
    public InventoryPushConsumer(IMallInventoryApi mall, ConsumerDependencies deps) : base(deps) => _mall = mall;

    protected override string DedupKey(InventoryChanged m) => $"inv:{m.TenantId}:{m.Sku}:{m.ChangedAt:O}";

    protected override async Task HandleAsync(ConsumeContext<InventoryChanged> ctx)
    {
        var m = ctx.Message;
        await _mall.UpdateStockAsync(m.TenantId, m.Sku, m.Available, ctx.CancellationToken);
    }
}

public interface IMallInventoryApi { Task UpdateStockAsync(string tenant, string sku, int available, CancellationToken ct); }

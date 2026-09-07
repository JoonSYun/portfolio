using Portfolio.IntegrationPlatform.Consistency;
using Portfolio.IntegrationPlatform.CoreConnectorLayers.Core;
using Portfolio.IntegrationPlatform.ResultModel;
using Portfolio.IntegrationPlatform.RuleConfiguration;

namespace Portfolio.IntegrationPlatform.CoreConnectorLayers.Connectors.Sabangnet;

/// <summary>
/// [담당업무 1] 커넥터 계층 — 첫 커넥터, 쇼핑몰 통합관리 솔루션(사방넷) 주문 연동.
///
/// 보이는 게 전부다: 설정·인증·로깅·예외·전송 보장은 <see cref="ConnectorBase{TRequest,TItem}"/> 에 있다.
/// 이 클래스는 "사방넷 주문을 WMS 출고 지시로 만든 뒤 사방넷에 접수 상태를 돌려준다"는 업무 흐름만 안다.
/// </summary>
public sealed class SabangnetOrderConnector : ConnectorBase<SabangnetOrderRequest, SabangnetOrder>
{
    private readonly ISabangnetClient _sabangnet;
    private readonly IWmsOutboundService _wms;
    private readonly WmsFirstOrderingPolicy _ordering;

    public SabangnetOrderConnector(ILogger<SabangnetOrderConnector> log, IRuleConfigProvider rules, IConnectorAuthProvider auth,
        IOutboxWriter outbox, ISabangnetClient sabangnet, IWmsOutboundService wms, WmsFirstOrderingPolicy ordering)
        : base(log, rules, auth, outbox) { _sabangnet = sabangnet; _wms = wms; _ordering = ordering; }

    public override string ConnectorCode => "SABANGNET";

    protected override async Task ExecuteAsync(SabangnetOrder order, RuleConfigSnapshot cfg, ConnectorCredential cred, ItemResult result, CancellationToken ct)
    {
        // 단계 1: 매핑 — 브랜드·거래처별 분기는 코드가 아니라 설정에서 온다 (2.RuleConfiguration)
        var shipMethod = cfg.CodeMap("ShipMethod").Map(order.DeliveryType);          // 사방넷 코드 → WMS 코드
        var skuMapping = cfg.CodeMap("Sku");
        var allowedWarehouses = cfg.List("AllowedWarehouses");
        var outbound = new OutboundInstruction(order.OrderNo, order.Lines.Select(l => (skuMapping.Map(l.MallSku), l.Qty)).ToList(), shipMethod);
        result.Pass(Stage.Map, $"배송={shipMethod}, {outbound.Lines.Count}라인");

        if (!allowedWarehouses.Contains(order.WarehouseCode))
        {
            result.Fail(Stage.Validate, $"허용되지 않은 창고 {order.WarehouseCode}");
            return;
        }
        result.Pass(Stage.Validate);

        // 단계 2·3: WMS 우선 → 외부 반영. 순서는 정책 객체가 고정한다 (4.Consistency)
        await _ordering.ExecuteAsync(
            wmsAction: async c => { await _wms.CreateOutboundAsync(outbound, c); result.Pass(Stage.WmsCommit, outbound.OrderNo); },
            externalAction: async c => { await _sabangnet.AcknowledgeOrderAsync(cred, order.OrderNo, c); result.Pass(Stage.ExternalCommit); },
            onExternalFailure: (ex, c) => { result.Fail(Stage.ExternalCommit, ex.Message, (ex as ConnectorException)?.RemoteCode); return Task.CompletedTask; },
            ct);
    }
}

// ---- 사방넷 전문 (REST/JSON) ----------------------------------------------------

public sealed record SabangnetOrderRequest(string BrandCode, string CenterCode, string ServiceCode, IReadOnlyList<SabangnetOrder> Items)
    : IIntegrationRequest<SabangnetOrder>;

public sealed record SabangnetOrder(string OrderNo, string DeliveryType, string WarehouseCode, IReadOnlyList<SabangnetOrderLine> Lines) : IIntegrationItem
{
    public string Key => OrderNo;
}
public sealed record SabangnetOrderLine(string MallSku, int Qty);

public interface ISabangnetClient
{
    Task<IReadOnlyList<SabangnetOrder>> FetchNewOrdersAsync(ConnectorCredential cred, string brand, DateTime sinceUtc, CancellationToken ct);
    Task AcknowledgeOrderAsync(ConnectorCredential cred, string orderNo, CancellationToken ct);
}

public interface IWmsOutboundService { Task CreateOutboundAsync(OutboundInstruction instruction, CancellationToken ct); }
public sealed record OutboundInstruction(string OrderNo, IReadOnlyList<(string Sku, int Qty)> Lines, string ShipMethod);

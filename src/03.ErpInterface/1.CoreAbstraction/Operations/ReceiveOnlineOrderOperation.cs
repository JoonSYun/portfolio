using System.Collections.Generic;
using System.Linq;

namespace Portfolio.ErpInterface.CoreAbstraction.Operations
{
    /// <summary>
    /// 수신 오퍼레이션 예시 — 온라인 주문 수신 (수신 15종 중 하나).
    /// 베이스가 검증·복원·예외 변환을 하므로, 여기엔 이 전문에만 해당하는 규칙과 처리만 남는다.
    /// </summary>
    public sealed class ReceiveOnlineOrderOperation : InterfaceOperationBase<OnlineOrderRequest, OnlineOrderResponse>
    {
        private readonly IOrderRepository _orders;
        public ReceiveOnlineOrderOperation(IOrderRepository orders) { _orders = orders; }

        public override string OperationCode => "IF_ORD_011";

        protected override IEnumerable<ValidationError> Validate(OnlineOrderRequest r) => OnlineOrderRules.Validate(r);

        protected override void Restore(OnlineOrderRequest r)
        {
            base.Restore(r);
            foreach (var o in r.Orders)
            {
                o.ShipMethod = string.IsNullOrWhiteSpace(o.ShipMethod) ? "PARCEL" : o.ShipMethod.Trim().ToUpperInvariant();
                foreach (var l in o.Lines) l.Sku = l.Sku.Trim().ToUpperInvariant();   // ERP 는 소문자·공백을 섞어 보낸다
            }
        }

        protected override OnlineOrderResponse Handle(OnlineOrderRequest r)
        {
            var accepted = _orders.UpsertBatch(r.BrandCode, r.Orders);   // 재전송 대비 멱등 upsert
            return new OnlineOrderResponse { AcceptedCount = accepted, Total = r.Orders.Count };
        }
    }

    /// <summary>주문 전문 규칙 — 오퍼레이션과 검증기(2.Verification)가 같은 규칙을 공유한다.</summary>
    public static class OnlineOrderRules
    {
        public static IEnumerable<ValidationError> Validate(OnlineOrderRequest r)
        {
            if (r.Orders == null || r.Orders.Count == 0) yield return new ValidationError("Orders", "1건 이상");
            foreach (var o in r.Orders ?? new List<OnlineOrderDto>())
            {
                if (o.Lines.Any(l => l.Qty <= 0)) yield return new ValidationError(o.OrderNo + ".Qty", "0 이하");
                if (o.Lines.Sum(l => l.Qty) > 999) yield return new ValidationError(o.OrderNo, "라인 수량 합 초과");
            }
        }
    }

    public sealed class OnlineOrderRequest : ErpRequestBase { public List<OnlineOrderDto> Orders { get; set; } }
    public sealed class OnlineOrderResponse : ErpResponseBase { public int AcceptedCount { get; set; } public int Total { get; set; } }
    public sealed class OnlineOrderDto { public string OrderNo { get; set; } public string ShipMethod { get; set; } public List<OrderLineDto> Lines { get; set; } }
    public sealed class OrderLineDto { public string Sku { get; set; } public int Qty { get; set; } }
    public interface IOrderRepository { int UpsertBatch(string brand, List<OnlineOrderDto> orders); }

    /// <summary>조회 오퍼레이션 예시 — 재고 조회 (조회 9종 중 하나). 처리만 있다.</summary>
    public sealed class QueryStockOperation : InterfaceOperationBase<StockQueryRequest, StockQueryResponse>
    {
        private readonly IStockRepository _stock;
        public QueryStockOperation(IStockRepository stock) { _stock = stock; }
        public override string OperationCode => "IF_STK_003";
        protected override StockQueryResponse Handle(StockQueryRequest r) =>
            new StockQueryResponse { Items = _stock.Query(r.BrandCode, r.Skus) };
    }

    public sealed class StockQueryRequest : ErpRequestBase { public List<string> Skus { get; set; } }
    public sealed class StockQueryResponse : ErpResponseBase { public List<StockDto> Items { get; set; } }
    public sealed class StockDto { public string Sku { get; set; } public int Available { get; set; } public int Reserved { get; set; } }
    public interface IStockRepository { List<StockDto> Query(string brand, List<string> skus); }
}

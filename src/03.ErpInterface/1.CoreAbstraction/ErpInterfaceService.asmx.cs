using System.Web.Services;
using Portfolio.ErpInterface.CoreAbstraction.Operations;

namespace Portfolio.ErpInterface.CoreAbstraction
{
    /// <summary>
    /// [담당업무 1] ASMX/SOAP 엔드포인트 — 총 24개 오퍼레이션 (조회 9 · 수신 15).
    /// 각 WebMethod 는 오퍼레이션 객체에 위임하는 한 줄이다. 규격·검증·예외 처리는 베이스에 있다.
    /// </summary>
    [WebService(Namespace = "urn:wms:erp-interface:v1.2")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    public sealed class ErpInterfaceService : WebService
    {
        private readonly OperationCatalog _ops = OperationCatalog.Instance;

        // ---- 조회 9종 (ERP → WMS 질의) ----
        [WebMethod] public StockQueryResponse QueryStock(StockQueryRequest req) => _ops.Get<QueryStockOperation>().Execute(req);
        // [WebMethod] QueryInbound / QueryOutbound / QueryReturn / QueryItemMaster / QueryLocation / QueryShipment / QueryInvoice / QueryClosing

        // ---- 수신 15종 (ERP → WMS 데이터 적재) ----
        [WebMethod] public OnlineOrderResponse ReceiveOnlineOrder(OnlineOrderRequest req) => _ops.Get<ReceiveOnlineOrderOperation>().Execute(req);
        // [WebMethod] ReceiveItemMaster / ReceiveCustomerMaster / ReceiveVendorMaster / ReceiveInboundPlan / ReceiveOutboundPlan
        //             ReceiveReturnRequest / ReceiveReturnCancel / ReceiveOrderCancel / ReceivePriceMaster / ReceivePromotion
        //             ReceiveWarehouseMaster / ReceiveShipMethodMaster / ReceiveHolidayMaster / ReceiveClosingConfirm
    }

    /// <summary>오퍼레이션 코드 ↔ 구현 카탈로그. 더미데이터 생성기·검증기도 이 카탈로그로 스키마를 찾는다.</summary>
    public sealed class OperationCatalog
    {
        public static OperationCatalog Instance { get; } = new OperationCatalog();
        public T Get<T>() where T : class => ServiceLocator.Resolve<T>();
    }

    internal static class ServiceLocator { public static T Resolve<T>() => default; }
}

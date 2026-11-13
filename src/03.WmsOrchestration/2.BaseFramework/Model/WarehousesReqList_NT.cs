using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Saga;
using System.Text.Json.Serialization;

namespace Portfolio.WmsOrchestration.Model
{
    public class WarehousesReqInsertEvent : ChunkStepDomainEventBase { }

    [MassTransitAutoBind(consumerType: nameof(ConsumerBindingType.CHUNK))]
    public class WarehousesReqList_NT : iMasterEntity
    {
        public string docNo { get; set; }
        [LengthCheck(2, 1)] public string bCode { get; set; }
        public int totalCount { get; set; }
        public int sendCount { get; set; }
        public int serialNo { get; set; }
        [LengthCheck(20, 1)] public string warehouseCode { get; set; }
        [LengthCheck(50, 1)] public string warehouseName { get; set; }
        [LengthCheck(1, 1)] public string managedYN { get; set; }
        [LengthCheck(255)] public string? remark { get; set; }
        [LengthCheck(1, 1)] public string endYN { get; set; }
        [LengthCheck(255)] public string? extColumn1 { get; set; }
        [LengthCheck(255)] public string? extColumn2 { get; set; }
        public int? extColumn3 { get; set; }
    }

    public class WareHouseResponseList
    {
        [JsonPropertyName("warehouseCode")]
        public string? WAREHOUSE_CODE { get; set; }
        [JsonPropertyName("warehouseName")]
        public string? WAREHOUSE_NAME { get; set; }
        [JsonPropertyName("managedYn")]
        public string? MANAGED_YN { get; set; }
        [JsonPropertyName("endYn")]
        public string? END_YN { get; set; }
        [JsonPropertyName("resultCode")]
        public string? RESULT_CODE { get; set; }
    }
    public class WareHouseDetailResponseList
    {
        [JsonPropertyName("warehouseCode")]
        public string WAREHOUSE_CODE { get; set; }
        [JsonPropertyName("warehouseName")]
        public string WAREHOUSE_NAME { get; set; }
        [JsonPropertyName("managedYn")]
        public string MANAGED_YN { get; set; }
        [JsonPropertyName("remark")]
        public string REMARK { get; set; }
        [JsonPropertyName("endYn")]
        public string END_YN { get; set; }
        [JsonPropertyName("extColumn1")]
        public string EXT_COLUMN1 { get; set; }
        [JsonPropertyName("extColumn2")]
        public string EXT_COLUMN2 { get; set; }
        [JsonPropertyName("extColumn3")]
        public int EXT_COLUMN3 { get; set; }
        [JsonPropertyName("bCode")]
        public string BCODE { get; set; }
        [JsonPropertyName("reqDatetime")]
        public string REQ_DATETIME { get; set; }
    }
}

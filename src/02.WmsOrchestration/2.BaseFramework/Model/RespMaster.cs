using System.Text.Json.Serialization;

namespace Portfolio.WmsOrchestration.Model
{
    public class OrderResult<TRequest, TResponse, TDetail>
    {
        public TRequest? req { get; set; }
        public TResponse? resp { get; set; }
        public IEnumerable<TDetail>? Details { get; set; }
    }
    public class OrderResult<TRequest, TReqDetail, TRespDetail, TRespDetail2>
    {
        public TRequest? req { get; set; }
        public IEnumerable<TReqDetail>? reqDetails { get; set; }
        public IEnumerable<TRespDetail>? respDetails { get; set; }
        public IEnumerable<TRespDetail2>? respDetails2 { get; set; }
    }

    public class MasterWithDetails<TMaster, TDetail>
    {
        public TMaster? Master { get; set; }
        public IEnumerable<TDetail>? Details { get; set; }
    }

    public class RespMasterDetailList<TMaster, TDetail>
    {
        public int TotalCount { get; set; } // Master 전체 카운트
        public IEnumerable<MasterWithDetails<TMaster, TDetail>>? Masters { get; set; }
    }

    public class RespMaster<T>
    {
        public int totalCount { get; set; }
        public List<T> datas { get; set; }
    }

    public class RespDetail<T, HistoryData>
    {
        public T masterData { get; set; }
        public List<HistoryData> datas { get; set; }
    }

    public class HistoryData
    {
        [JsonPropertyName("resultCode")]
        public string result_code { get; set; }
        [JsonPropertyName("reqDatetime")]
        public string req_datetime { get; set; }
        [JsonPropertyName("errorMsg")]
        public string ERROR_MSG { get; set; }
    }
}

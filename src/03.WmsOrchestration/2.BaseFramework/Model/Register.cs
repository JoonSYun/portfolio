using System.Text.Json.Serialization;

namespace Portfolio.WmsOrchestration.Model
{
    public class Register
    {
        public class RegisterReq
        {
            public string docNo { get; set; }
            public string bCode { get; set; }
            public int? totalCount { get; set; }
            public int? sendCount { get; set; }
            public string? clientId { get; set; }
        }
        public class RegisterResp
        {
            public string docNo { get; set; }
            public string resultCode { get; set; }
            public string resultMessage { get; set; }
            public string wmsDateTime { get; set; }
            public int? succCount { get; set; }
            public int? errorCount { get; set; }
            public List<RegisterRespErrorList>? error { get; set; }
        }

        public class RegisterRespErrorList
        {
            public int serialNo { get; set; }
            public string errorCode { get; set; }
            public string message { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string apiColumn1 { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string apiColumn2 { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string apiColumn3 { get; set; }
        }
    }
}

namespace Portfolio.WmsOrchestration.Model
{
    public class JobCompleteNotification : INotification
    {
        public string UserId { get; set; }
        public string NotificationType { get; set; }
        public Guid JobId { get; set; } 
        public string JobIdentifier { get; set; }
        public string DomainType { get; set; }
        public string? RequestBrandCode { get; set; }
        public int TotalChunkCount { get; set; }
        public string? SlipDiv { get; set; } = "";    // 전표 구분
        public string? Delimiter { get; set; } = "";  // JobDiv
        public string IsSuccess { get; set; }   // job_master Status
        public bool IsBypass { get; set; }
        public string? Message { get; set; } = "";
        public object? ResponsePayload { get; set; } = null;
    }
}

namespace Portfolio.WmsOrchestration.Gateway.Model
{
    public class ApiRateLimitOptions
    {
        public int PermitLimit { get; set; }
        public int WindowSeconds { get; set; }
        public int QueueLimit { get; set; }
    }
}

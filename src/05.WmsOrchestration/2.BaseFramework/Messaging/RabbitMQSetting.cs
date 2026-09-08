namespace Portfolio.WmsOrchestration.Model
{
    public class RabbitMQSetting
    {
        public bool IsActive { get; set; }
        public int ConsumerConcurrency { get; set; }
        public int ServerConcurrency { get; set; }
        public int XMaxPriority { get; set; }
        public int QueryDelaySeconds { get; set; }
        public int DuplicateDetectionWindowMinutes { get; set; }
        public int MessageDeliveryLimit { get; set; }
        public int MessageDeliveryTimeoutSeconds { get; set; }
        public int DBContextTimeoutSeconds { get; set; }
        public int MessageRetryCount { get; set; }
    }
}

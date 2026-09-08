namespace Portfolio.WmsOrchestration.Model
{
    public class JobCompleteBypassNotification : INotification
    {
        public string UserId { get; set; }
        public string NotificationType { get; set; }
        public BypassResponse BypassResponse { get; set; }
    }
}

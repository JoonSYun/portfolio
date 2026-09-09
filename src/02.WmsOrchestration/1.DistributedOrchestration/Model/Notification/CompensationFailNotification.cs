using Portfolio.WmsOrchestration.Saga;

namespace Portfolio.WmsOrchestration.Model
{
    public class CompensationFailNotification : INotification
    {
        public string UserId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public string NotificationType { get; set; }
        public CompensationFailAlertEvent? FailEvent { get; set; }
    }
}

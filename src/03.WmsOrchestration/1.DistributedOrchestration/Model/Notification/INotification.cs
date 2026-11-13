using Portfolio.WmsOrchestration.Infrastructure;

namespace Portfolio.WmsOrchestration.Model
{
    public interface INotification
    {
        Guid NotificationId { get { return IdentifierGenerator.GetSequentialGUID(); } }
        string EventType { get { return GetType().Name; } }
        DateTime CreatedAt { get { return TimeHelper.GetNow(); } }
        string UserId { get; set; }
        string NotificationType { get; set; }
    }
}

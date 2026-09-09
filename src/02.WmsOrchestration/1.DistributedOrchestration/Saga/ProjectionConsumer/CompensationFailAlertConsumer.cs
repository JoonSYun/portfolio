using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Service;

namespace Portfolio.WmsOrchestration.Saga
{
    public class CompensationFailAlertConsumer : IConsumer<CompensationFailAlertEvent>
    {
        private readonly ILogger<CompensationFailAlertConsumer> _logger;
        private readonly NotificationService _notificationService;

        public CompensationFailAlertConsumer(
            ILogger<CompensationFailAlertConsumer> logger,
            NotificationService notificationService
            )
        {
            _logger = logger;
            _notificationService = notificationService;
        }

        public async Task Consume(ConsumeContext<CompensationFailAlertEvent> context)
        {
            try
            {
                var notification = new CompensationFailNotification
                {
                    UserId = context.Message.UserId,
                    NotificationType = nameof(CompensationFailNotification),
                    FailEvent = context.Message
                };

                await _notificationService.SendAsync(notification);
                await _notificationService.SendToRolesAsync(notification, UserRoleType.ADMIN, UserRoleType.DEVELOPER);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, AppLog.Log("[{ConsumerType}] Error Alert conpensation fail"), GetType().Name);

                throw;
            }
        }
    }
}

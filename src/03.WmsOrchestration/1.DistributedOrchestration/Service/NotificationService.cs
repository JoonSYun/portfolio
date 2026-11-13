using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Service
{
    /// <summary>
    /// SignalR-based Notification Delivery Service
    /// -------------------------------------------
    ///
    /// ROLE
    /// ----
    /// Provides a unified abstraction to deliver real-time notifications
    /// over SignalR to:
    ///   - specific users
    ///   - role-based groups
    ///   - center-based groups
    ///   - brand-based groups
    ///   - entire system (broadcast)
    ///
    /// MODEL
    /// -----
    /// All notifications implement <see cref="INotification"/>.
    ///
    /// Key common fields expected:
    ///   - UserId            → target user (if applicable)
    ///   - EventType         → business event name
    ///   - NotificationType  → delivery type (User/Role/Center/etc.)
    ///   - Payload           → business data for UI
    ///
    /// WHY THIS SERVICE EXISTS
    /// -----------------------
    /// Instead of calling HubContext directly everywhere,
    /// this centralized service ensures:
    ///
    ///   ✔ consistent logging format
    ///   ✔ unified group naming convention
    ///   ✔ validation (e.g. missing UserId)
    ///   ✔ future extensibility (audit log, persistence, retry)
    ///
    /// USAGE EXAMPLES
    /// --------------
    ///   await _notificationService.SendAsync(notification);
    ///   await _notificationService.SendToRolesAsync(notification, UserRoleType.ADMIN);
    ///   await _notificationService.SendToCentersAsync(notification, "C1001");
    ///   await _notificationService.BroadcastAsync(notification);
    ///
    /// </summary>
    public class NotificationService
    {
        private readonly IHubContext<OrchestrationHub> _hubContext;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            IHubContext<OrchestrationHub> hubContext,
            ILogger<NotificationService> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        #region Individual Notifications

        /// <summary>
        /// Standard fan-out: send notification to a user + admin/dev + brand group.
        ///
        /// WHY THIS PATTERN:
        /// -----------------
        /// - End user receives their notification.
        /// - Admin/Developer roles monitor system behavior.
        /// - Brand group users receive contextual updates.
        ///
        /// This avoids duplicating these 3 sends across the entire system.
        /// </summary>
        public async Task SendStandardAsync(INotification notification, string requestBrandCode = "DEFAULT")
        {
            ValidateUserId(notification);

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    notification.NotificationType = NotificationType.INDIVIDUAL.ToString();

                    await SendAsync(notification);
                    await SendToRolesAsync(notification, UserRoleType.ADMIN, UserRoleType.DEVELOPER);
                    await SendToCompaniesAsync(notification, requestBrandCode);
                },
                "Sent to user and brand group: UserId={UserId}, BrandCode={BrandCode}, Type={Type}",
                "Failed to send: UserId={UserId}, BrandCode={BrandCode}",
                notification.UserId, requestBrandCode, notification.EventType);
        }

        /// <summary>
        /// Sends notification to a single user.
        ///
        /// BEHAVIOR:
        /// ---------
        /// - Requires UserId.
        /// - Client receives via: ReceiveNotification(notification).
        ///
        /// WHY VALIDATION:
        /// ---------------
        /// Avoids silent failures due to missing UserId,
        /// which is one of the most common integration mistakes.
        /// </summary>
        public async Task SendAsync(INotification notification)
        {
            ValidateUserId(notification);

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    notification.NotificationType = NotificationType.INDIVIDUAL.ToString();
                    await _hubContext.Clients.User(notification.UserId)
                        .SendAsync("ReceiveNotification", notification);
                },
                "Sent to user: UserId={UserId}, Type={Type}",
                "Failed to send: UserId={UserId}",
                notification.UserId, notification.EventType);
        }

        /// <summary>
        /// Sends many user-specific notifications in bulk.
        ///
        /// PERFORMANCE NOTE:
        /// -----------------
        /// - Parallelism is intentionally avoided.
        /// - SignalR sends are relatively light-weight,
        ///   and batching improves readability & control.
        ///
        /// VALIDATION:
        /// -----------
        /// Filters invalid entries (no UserId) instead of throwing,
        /// because a batch typically receives generated data.
        /// </summary>
        public async Task SendBatchAsync(IEnumerable<INotification> notifications)
        {
            var validNotifications = notifications
                .Where(n => !string.IsNullOrWhiteSpace(n.UserId))
                .ToList();

            if (!validNotifications.Any())
            {
                _logger.LogWarning(AppLog.Log("[NotificationService] No valid notifications to send in batch"));
                return;
            }

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    foreach (var n in validNotifications)
                        n.NotificationType = NotificationType.INDIVIDUAL.ToString();

                    var tasks = validNotifications.Select(n =>
                        _hubContext.Clients.User(n.UserId).SendAsync("ReceiveNotification", n));

                    await Task.WhenAll(tasks);
                },
                "Sent batch: Count={Count}, Users={@Users}",
                "Failed to send batch",
                validNotifications.Count, validNotifications.Select(r => r.UserId).ToList());
        }

        #endregion

        #region Role-based Notifications

        /// <summary>
        /// Sends notification to all users that belong to any of the given roles.
        ///
        /// GROUP CONVENTION:
        /// -----------------
        /// Role groups map directly to role names.
        ///
        /// Example:
        ///   Roles: ADMIN, MANAGER
        ///   Groups: "ADMIN", "MANAGER"
        ///
        /// WHY ROLE GROUPS?
        /// ----------------
        /// - Avoids querying DB per broadcast.
        /// - SignalR group membership is managed during login lifecycle.
        /// </summary>
        public async Task SendToRolesAsync(INotification notification, params UserRoleType[] roles)
        {
            if (roles == null || roles.Length == 0)
            {
                _logger.LogWarning(AppLog.Log("[NotificationService] No roles specified"));
                return;
            }

            notification.NotificationType = NotificationType.ROLE.ToString();

            var groups = roles.Select(r => r.ToString()).ToList();

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    await _hubContext.Clients.Groups(groups).SendAsync("ReceiveNotification", notification);
                },
                "Sent to roles: Roles={Roles}, Type={Type}",
                "Failed to send to roles: Roles={Roles}",
                string.Join(", ", groups), notification.EventType);
        }

        #endregion

        #region Center-based Notifications

        /// <summary>
        /// Sends notification to all users assigned to the given centers.
        ///
        /// GROUP NAMING RULE:
        /// ------------------
        /// Center groups are prefixed:
        ///
        ///   Center-{CenterId}
        ///
        /// Example:
        ///   Center-101, Center-203
        ///
        /// This convention keeps namespace separation clear and predictable.
        /// </summary>
        public async Task SendToCentersAsync(INotification notification, params string[] centerIds)
        {
            if (centerIds == null || centerIds.Length == 0)
            {
                _logger.LogWarning(AppLog.Log("[NotificationService] No centerIds specified"));
                return;
            }

            notification.NotificationType = NotificationType.CENTER.ToString();

            var groups = centerIds.Select(id => $"Center-{id}").ToList();

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    await _hubContext.Clients.Groups(groups).SendAsync("ReceiveNotification", notification);
                },
                "Sent to centers: CenterIds={CenterIds}, Type={Type}",
                "Failed to send to centers: CenterIds={CenterIds}",
                string.Join(", ", centerIds), notification.EventType);
        }

        #endregion

        #region Brand-based Notifications

        /// <summary>
        /// Sends notification to users associated with the given companies.
        ///
        /// GROUP RULE:
        /// -----------
        /// Brand group naming:
        ///
        ///   Brand-{BrandCode}
        ///
        /// WHY:
        /// ----
        /// Allows UI to subscribe to business-specific scopes easily.
        /// </summary>
        public async Task SendToCompaniesAsync(INotification notification, params string[] companyIds)
        {
            if (companyIds == null || companyIds.Length == 0)
            {
                _logger.LogWarning(AppLog.Log("[NotificationService] No companyIds specified"));
                return;
            }

            notification.NotificationType = NotificationType.BRAND.ToString();

            var groups = companyIds.ToList();

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    await _hubContext.Clients.Groups(groups).SendAsync("ReceiveNotification", notification);
                },
                "Sent to brands: CompanyIds={CompanyIds}, Type={Type}",
                "Failed to send to brands: CompanyIds={CompanyIds}",
                string.Join(", ", companyIds), notification.EventType);
        }

        #endregion

        #region Broadcast Notifications

        /// <summary>
        /// Sends notification to every connected client in the system.
        ///
        /// WARNING (best practice):
        /// ------------------------
        /// Broadcast should be used sparingly.
        /// Prefer:
        ///   - Role broadcasts
        ///   - Brand broadcasts
        ///   - Center broadcasts
        ///
        /// Broadcasting to all connected users can:
        ///   - overload UI clients unnecessarily
        ///   - expose irrelevant business context
        /// </summary>
        public async Task BroadcastAsync(INotification notification)
        {
            notification.NotificationType = NotificationType.BRODCAST.ToString();

            await ExecuteWithLoggingAsync(
                async () =>
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", notification);
                },
                "Broadcast sent: Type={Type}",
                "Failed to broadcast",
                notification.EventType);
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Ensures a notification targeting a specific user has UserId populated.
        ///
        /// WHY:
        /// ----
        /// - Prevents silent failures
        /// - Encourages upstream systems to always set target identity
        /// </summary>
        private void ValidateUserId(INotification notification)
        {
            if (string.IsNullOrWhiteSpace(notification.UserId))
            {
                _logger.LogWarning(
                    AppLog.Log("[NotificationService] UserId is empty in notification: Type={Type}"),
                    notification.EventType);

                throw new ArgumentException("UserId is required in notification", nameof(notification));
            }
        }

        /// <summary>
        /// Wraps execution with consistent logging and exception handling.
        ///
        /// BENEFITS:
        /// ---------
        /// - Every operation logs success/failure uniformly.
        /// - Central place to later:
        ///     * add retry behavior
        ///     * push audit logs to DB
        ///     * add metrics / tracing
        /// </summary>
        private async Task ExecuteWithLoggingAsync(
            Func<Task> action,
            string successLogTemplate,
            string errorLogTemplate,
            params object[] logArgs)
        {
            try
            {
                await action();
                _logger.LogInformation(AppLog.Log($"[NotificationService] {successLogTemplate}"), logArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, AppLog.Log($"[NotificationService] {errorLogTemplate}"), logArgs);
                throw;
            }
        }

        #endregion
    }

}

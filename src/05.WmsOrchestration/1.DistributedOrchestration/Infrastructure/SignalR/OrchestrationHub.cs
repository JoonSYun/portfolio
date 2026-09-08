using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Repository;
using System.Security.Claims;
using System.Text.RegularExpressions;

[Authorize]
public class OrchestrationHub : Hub
{
    private readonly ILogger<OrchestrationHub> _logger;
    private readonly OrchestrationUserQueryRepository _userQueryRepository;

    public OrchestrationHub(
        ILogger<OrchestrationHub> logger,
        OrchestrationUserQueryRepository userQueryRepository)
    {
        _logger = logger;
        _userQueryRepository = userQueryRepository;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var connId = Context.ConnectionId;

        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning(
                AppLog.Log("[OrchestrationHub] Connection rejected: No UserId"));

            throw new UnauthorizedAccessException("No UserId found in JWT");
        }

        var userInfo = await _userQueryRepository.GetSignalRUserInfo(userId);

        // Group by RoleNames and CompanyCodes
        foreach (var role in userInfo.RoleNames)
        {
            await Groups.AddToGroupAsync(connId, role);
        }

        foreach (var company in userInfo.CompanyCodes)
        {
            await Groups.AddToGroupAsync(connId, company);
        }

        _logger.LogInformation(
            AppLog.Log("[OrchestrationHub] User connected: UserId={UserId}, ConnectionId={ConnectionId}, Roles={@Roles}, Companies={@Companies}"),
            userId, connId, userInfo.RoleNames, userInfo.CompanyCodes);

        await base.OnConnectedAsync();
    }
}

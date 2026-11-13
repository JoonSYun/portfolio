using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.Admin.Infrastructure.Scheduler;

namespace Portfolio.Admin.Controllers.Scheduler;

/// <summary>
/// "Run now" 수동 실행 프록시. 잡 enqueue 는 스케줄러 프로세스의 Hangfire 클라이언트가 필요하므로
/// Admin 은 본 요청을 내부망 스케줄러(<c>POST /api/admin/runs/*</c>)로 그대로 포워딩한다.
/// 과거 브라우저가 공개 FQDN 으로 직접 치던 경로를 Admin origin + [Authorize] 뒤로 옮기기 위한 표면.
/// </summary>
[ApiController]
[Route("api/admin/runs")]
[Authorize]
public sealed class RunsProxyController : ControllerBase
{
    private readonly SchedulerProxyClient _proxy;

    public RunsProxyController(SchedulerProxyClient proxy) => _proxy = proxy;

    [HttpPost("brand/{domainCode}/{brandCode}")]
    public Task RunBrand(string domainCode, string brandCode) => _proxy.ForwardAsync(HttpContext);

    [HttpPost("domain/{code}")]
    public Task RunDomain(string code) => _proxy.ForwardAsync(HttpContext);
}

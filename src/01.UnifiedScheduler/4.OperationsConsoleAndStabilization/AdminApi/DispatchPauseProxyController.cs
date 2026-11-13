using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.Admin.Infrastructure.Scheduler;

namespace Portfolio.Admin.Controllers.Scheduler;

/// <summary>
/// Dispatch 전역 일시정지 조회/토글 프록시. 상태는 스케줄러 프로세스의 in-memory 캐시 + 로컬 파일
/// (DispatchPauseStore) 이 진실이며 Dispatcher.Fire 가 hot-path 로 읽으므로, Admin 이 파일을 직접
/// 쓰지 않고 반드시 스케줄러의 <c>GET/POST /api/admin/dispatch-pause</c> 로 포워딩한다.
/// </summary>
[ApiController]
[Route("api/admin/dispatch-pause")]
[Authorize]
public sealed class DispatchPauseProxyController : ControllerBase
{
    private readonly SchedulerProxyClient _proxy;

    public DispatchPauseProxyController(SchedulerProxyClient proxy) => _proxy = proxy;

    [HttpGet]
    public Task Get() => _proxy.ForwardAsync(HttpContext);

    [HttpPost]
    public Task Set() => _proxy.ForwardAsync(HttpContext);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.Admin.Infrastructure;
using Portfolio.Admin.Models;

namespace Portfolio.Admin.Controllers.Scheduler;

/// <summary>
/// 관리자 대시보드용 단일 진입 컨트롤러. 화면 한 번 로딩에 필요한 카드 데이터를 묶어 한 envelope 로 내려준다.
/// DispatchPause 상태는 Scheduler 솔루션 소유라 프론트가 별도 endpoint(<c>/api/admin/dispatch-pause</c>) 와
/// 본 endpoint 를 병렬로 받아 합성한다.
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly DashboardRepository _repo;

    public DashboardController(DashboardRepository repo)
    {
        _repo = repo;
    }

    [HttpGet("summary")]
    public ActionResult<ApiResponse<DashboardSummaryDto>> GetSummary()
        => Ok(ApiResponse<DashboardSummaryDto>.Ok(_repo.GetSummary(), HttpContext));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.Admin.Infrastructure;
using Portfolio.Admin.Models;

namespace Portfolio.Admin.Controllers.Scheduler;

/// <summary>
/// 잡 로그 조회 전용. 강제 종료(deprecate) 액션은 Hangfire 클라이언트가 필요해 Scheduler 솔루션에 남아있다 —
/// 동일 prefix 의 동일 path 라도 HTTP method 가 달라 라우팅 충돌은 없지만, 의도적으로 Scheduler 측의
/// <c>POST /api/admin/job-logs/{id}/deprecate</c> 는 본 컨트롤러에 두지 않는다.
/// </summary>
[ApiController]
[Route("api/admin/job-logs")]
[Authorize]
public class JobLogsController : ControllerBase
{
    private readonly JobLogReadRepository _repo;

    public JobLogsController(JobLogReadRepository repo)
    {
        _repo = repo;
    }

    [HttpGet]
    public ActionResult<ApiResponse<PagedResult<JobLogDto>>> Query([FromQuery] JobLogQuery q)
        => Ok(ApiResponse<PagedResult<JobLogDto>>.Ok(_repo.QueryLogs(q), HttpContext));

    /// <summary>LOG_ID 단건 조회 — 상세 모달 딥링크(?logId=). 없으면 404.</summary>
    [HttpGet("{logId}")]
    public ActionResult<ApiResponse<JobLogDto>> GetById(string logId)
    {
        var log = _repo.GetById(logId) ?? throw new NotFoundException("JobLog", logId);
        return Ok(ApiResponse<JobLogDto>.Ok(log, HttpContext));
    }
}

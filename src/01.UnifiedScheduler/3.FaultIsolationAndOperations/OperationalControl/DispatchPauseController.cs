using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Controllers;

/// <summary>
/// Admin UI 에서 Dispatch 전역 일시정지를 조회 / 토글하는 API.
/// 가드는 <see cref="Portfolio.UnifiedScheduler.Jobs.Dispatcher.Fire"/> 의 진입부에서만 동작하며,
/// 수동 실행(EnqueueOne / EnqueueDomain) 경로는 본 상태와 무관하게 흘러간다 — 점검 중에도 관리자 강제 실행을 보장.
/// </summary>
[ApiController]
[Route("api/admin/dispatch-pause")]
[Authorize] // 내부 전용. Admin 백엔드 프록시가 포워딩한 사용자 JWT 를 검증한다.
public class DispatchPauseController : ControllerBase
{
    private readonly DispatchPauseStore _store;

    public DispatchPauseController(DispatchPauseStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 현재 일시정지 상태 조회. 메모리 캐시에서 즉시 반환하므로 폴링 비용이 사실상 0.
    /// </summary>
    [HttpGet]
    public ActionResult<ApiResponse<DispatchPauseState>> Get()
        => Ok(ApiResponse<DispatchPauseState>.Ok(_store.Current, HttpContext));

    /// <summary>
    /// 일시정지 토글. <see cref="DispatchPauseRequest.IsPaused"/> 만 의미가 있으며, false 로 보낼 때는
    /// Reason 을 비워도 마지막 pause 시점의 메타데이터(누가 / 언제 / 사유)는 그대로 보존된다.
    /// 호출자(<see cref="DispatchPauseState.PausedBy"/>) 는 JWT name claim → DB UserName 우선,
    /// 없으면 sub / 없으면 "UNKNOWN" 으로 기록한다.
    /// </summary>
    [HttpPost]
    public ActionResult<ApiResponse<DispatchPauseState>> Set([FromBody] ApiRequest<DispatchPauseRequest> req)
    {
        var body = req.Data
                   ?? throw new BusinessRuleException("Request body required.",
                                                      StatusCodes.Status400BadRequest);

        var caller = ResolveCallerName();
        _store.Set(body.IsPaused, caller, body.Reason);
        return Ok(ApiResponse<DispatchPauseState>.Ok(_store.Current, HttpContext));
    }

    /// <summary>
    /// JWT 클레임에서 감사 로그용 식별자를 추출. Scheduler 는 토큰을 검증만 하므로 UserName 매핑은 토큰에 박힌
    /// name / sub 클레임을 신뢰한다. 인증 미적용 환경(테스트 등)에서 호출되면 "UNKNOWN".
    /// </summary>
    private string ResolveCallerName()
    {
        return User.FindFirstValue(ClaimTypes.Name)
            ?? User.FindFirstValue("name")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "UNKNOWN";
    }
}

/// <summary>
/// <see cref="DispatchPauseController.Set"/> 요청 바디.
/// </summary>
public class DispatchPauseRequest
{
    /// <summary>true 면 cron 자동 발사 차단. false 면 해제.</summary>
    public bool IsPaused { get; set; }

    /// <summary>일시정지 사유(자유 텍스트, 선택). pause = true 일 때만 의미가 있다.</summary>
    public string? Reason { get; set; }
}

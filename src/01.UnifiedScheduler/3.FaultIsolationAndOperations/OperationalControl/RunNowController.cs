using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.UnifiedScheduler.Jobs;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Controllers;

/// <summary>
/// Admin "Run now" 전용 — 단일 브랜드 / 도메인 fan-out 즉시 enqueue.
/// CRUD 책임을 갖는 BrandOverridesController / DomainsController 와 분리해 Dispatcher 의존성을
/// 한 곳에 모았다. 현재 두 엔드포인트만 있지만 향후 그룹 단위 run, payload 미리보기 dry-run 등
/// 수동 액션이 늘어나면 여기로 합류시킨다.
///
/// 활성 잡 중첩 방지 정책 (단일/fan-out 공통): ENQUEUED/RUNNING 잡 존재 시 그 (Domain,Brand) 만
/// skip 하고 결과 row 의 SkipReason 으로 노출 — HTTP 는 항상 200, 호출자가 본문 검사해 분기.
///
/// 활성 필터 정책: SCH_DOMAIN.USE_YN='N' 인 도메인은 manual run 도 차단 (400 BusinessRule). 그 외
/// 활성 필터(그룹/스케줄 RUN_AUTO_YN/override·brand USE_YN/날짜) 는 cron 자동 발사만 막는 의미이므로
/// manual 에서는 우회.
///
/// 테스트 path 정책: <see cref="RunAsMode"/> 가 필수 body 필드.
///   - runAs=test         → 실효 IsTest=true (ExecuteCoreAsync 스킵)
///   - runAs=operational  → 실효 IsTest = DB 측 TargetRow.IsTest (운영 의도지만 DB 가 Y 면 강제 test)
///   - DB=Y && runAs=operational (단일 단위) → 400. fan-out 은 부분 성공 정책으로 자동 test 흘림.
/// </summary>
[ApiController]
[Route("api/admin/runs")]
[Authorize] // 내부 전용. Admin 백엔드 프록시가 포워딩한 사용자 JWT 를 검증한다.
public class RunNowController : ControllerBase
{
    private readonly Dispatcher _dispatcher;

    public RunNowController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// JWT NameIdentifier claim 추출 — SCH_JOB_LOG.REQUESTER 컬럼에 저장될 발화자 식별자.
    /// SchedulerJwtExtensions 가 NameClaimType = NameIdentifier 로 매핑하므로 claim 부재 시
    /// User.Identity?.Name 도 동일 값. 둘 다 비면 인증 파이프라인 가정과 모순이라 "unknown"
    /// 으로 방어 (해당 row 는 사후 추적 가능하도록 명시 마커 유지).
    /// </summary>
    private string ResolveRequester() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "unknown";

    /// <summary>
    /// 단일 (Domain, Brand) 즉시 enqueue. cron 흐름과 동일한 Hangfire 경로 사용.
    /// SCH_DOMAIN.USE_YN 만 강제하고 나머지 활성 필터(그룹/스케줄 RUN_AUTO_YN/override·brand USE_YN/날짜) 는
    /// 우회 (관리자가 의식적으로 누른 액션 — 디버깅용).
    /// 매 호출마다 unique LogId 발급 → SCH_JOB_LOG 에 별도 row 생성.
    ///
    /// runAs 누락           → 400 (ModelState).
    /// 매핑 row 미존재       → NotFoundException → 404.
    /// SCH_DOMAIN.USE_YN='N' → BusinessRuleException → 400.
    /// DB IsTest=Y + runAs=Operational → BusinessRuleException → 400.
    /// 동일 (Domain,Brand) 활성 → 200 + SkipReason 채워진 DTO (skip).
    /// JobType 미등록 등 시스템 → InvalidOperationException → 500.
    /// </summary>
    [HttpPost("brand/{domainCode}/{brandCode}")]
    public ActionResult<ApiResponse<ManualRunDto>> RunBrand(
        string domainCode, string brandCode,
        [FromBody] ApiRequest<RunNowRequest> req)
    {
        var runAs = req.Data?.RunAs
                    ?? throw new BusinessRuleException(
                        "runAs is required ('test' or 'operational').",
                        StatusCodes.Status400BadRequest);

        var r = _dispatcher.EnqueueOne(domainCode, brandCode, runAs, ResolveRequester());
        return Ok(ApiResponse<ManualRunDto>.Ok(
            new ManualRunDto
            {
                DomainCode    = r.DomainCode,
                BrandCode     = r.BrandCode,
                LogId         = r.LogId,
                HangfireJobId = r.HangfireJobId,
                Queue         = r.Queue,
                SkipReason    = r.SkipReason,
                IsTest        = r.IsTest,
            },
            HttpContext));
    }

    /// <summary>
    /// 도메인의 모든 브랜드 오버라이드를 한 번에 fan-out enqueue.
    /// SCH_DOMAIN.USE_YN 만 강제하고 나머지 활성/그룹/날짜 필터는 우회. 매핑 0개여도 200 + 빈 리스트.
    /// 도메인 row 자체가 없으면 NotFoundException → 404.
    /// SCH_DOMAIN.USE_YN='N' 이면 BusinessRuleException → 400 (fan-out 전체 거부).
    /// 활성 잡 보유 브랜드는 skip 처리되어 응답 배열에 SkipReason 채워진 row 로 포함.
    /// runAs 는 fan-out 전체에 동일 적용 — DB 측 IsTest=Y 브랜드는 runAs 와 무관하게 강제 test.
    /// </summary>
    [HttpPost("domain/{code}")]
    public ActionResult<ApiResponse<IReadOnlyList<ManualRunDto>>> RunDomain(
        string code,
        [FromBody] ApiRequest<RunNowRequest> req)
    {
        var runAs = req.Data?.RunAs
                    ?? throw new BusinessRuleException(
                        "runAs is required ('test' or 'operational').",
                        StatusCodes.Status400BadRequest);

        var results = _dispatcher.EnqueueDomain(code, runAs, ResolveRequester())
            .Select(r => new ManualRunDto
            {
                DomainCode    = r.DomainCode,
                BrandCode     = r.BrandCode,
                LogId         = r.LogId,
                HangfireJobId = r.HangfireJobId,
                Queue         = r.Queue,
                SkipReason    = r.SkipReason,
                IsTest        = r.IsTest,
            })
            .ToList();
        return Ok(ApiResponse<IReadOnlyList<ManualRunDto>>.Ok(results, HttpContext));
    }
}

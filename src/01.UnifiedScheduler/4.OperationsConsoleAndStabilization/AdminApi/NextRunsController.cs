using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.Admin.Infrastructure;
using Portfolio.Admin.Models;

namespace Portfolio.Admin.Controllers.Scheduler;

/// <summary>
/// 도메인×브랜드 "다음 실행 예정" 브라우저. 브랜드 → 도메인 → 다음 발사 예정시각(Top N) 의
/// 3단 드릴다운을 위한 read-only 엔드포인트. 발사 시점 계산은 대시보드 "다음 실행 예정"
/// 카드와 동일한 Cronos 로직(<see cref="DashboardRepository"/>)을 재사용한다(활성 후보만, KST).
/// </summary>
[ApiController]
[Route("api/admin/next-runs")]
[Authorize]
public class NextRunsController : ControllerBase
{
    private readonly DashboardRepository _repo;

    public NextRunsController(DashboardRepository repo)
    {
        _repo = repo;
    }

    /// <summary>활성 발사 후보(override) 를 1건 이상 가진 브랜드 목록.</summary>
    [HttpGet("brands")]
    public ActionResult<ApiResponse<IReadOnlyList<NextRunBrandDto>>> GetBrands()
        => Ok(ApiResponse<IReadOnlyList<NextRunBrandDto>>.Ok(_repo.GetUpcomingBrands(), HttpContext));

    /// <summary>선택 브랜드가 활성 override 를 가진 도메인 목록 (브랜드 기준 탭).</summary>
    [HttpGet("brands/{brandCode}/domains")]
    public ActionResult<ApiResponse<IReadOnlyList<NextRunDomainDto>>> GetDomainsForBrand(string brandCode)
        => Ok(ApiResponse<IReadOnlyList<NextRunDomainDto>>.Ok(
            _repo.GetUpcomingDomainsForBrand(brandCode), HttpContext));

    /// <summary>활성 발사 후보(override) 를 1건 이상 가진 도메인 목록 (도메인 기준 탭).</summary>
    [HttpGet("domains")]
    public ActionResult<ApiResponse<IReadOnlyList<NextRunDomainDto>>> GetDomains()
        => Ok(ApiResponse<IReadOnlyList<NextRunDomainDto>>.Ok(_repo.GetUpcomingDomains(), HttpContext));

    /// <summary>선택 도메인이 활성 override 를 가진 브랜드 목록 (도메인 기준 탭).</summary>
    [HttpGet("domains/{domainCode}/brands")]
    public ActionResult<ApiResponse<IReadOnlyList<NextRunBrandDto>>> GetBrandsForDomain(string domainCode)
        => Ok(ApiResponse<IReadOnlyList<NextRunBrandDto>>.Ok(
            _repo.GetUpcomingBrandsForDomain(domainCode), HttpContext));

    /// <summary>
    /// 다음 발사 예정 시각 Top N. brand/domain 중 최소 1개 필수. 한쪽만 주면 그 축 전체 합산.
    /// </summary>
    [HttpGet("upcoming")]
    public ActionResult<ApiResponse<IReadOnlyList<DashboardUpcomingRunDto>>> GetUpcoming(
        [FromQuery] string? brand, [FromQuery] string? domain, [FromQuery] int top = 20)
        => Ok(ApiResponse<IReadOnlyList<DashboardUpcomingRunDto>>.Ok(
            _repo.GetUpcomingRuns(brand, domain, top), HttpContext));
}

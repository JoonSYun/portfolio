using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Controllers;

/// <summary>
/// 본 솔루션 내부 어셈블리를 reflection 으로 스캔해야 하는 카탈로그만 노출한다.
/// 단순 DB / 런타임 enum 카탈로그(domain-groups / timezones / job-statuses)는 Portfolio.Admin 으로 이전.
/// </summary>
[ApiController]
[Route("api/admin/catalog")]
[Authorize] // 내부 전용. Admin 백엔드 프록시가 포워딩한 사용자 JWT 를 검증한다.
public class CatalogController : ControllerBase
{
    private readonly JobCatalogService _catalog;

    public CatalogController(JobCatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("job-types")]
    public ActionResult<ApiResponse<IReadOnlyList<JobTypeItem>>> JobTypes()
        => Ok(ApiResponse<IReadOnlyList<JobTypeItem>>.Ok(_catalog.JobTypes, HttpContext));

    [HttpGet("param-dtos")]
    public ActionResult<ApiResponse<IReadOnlyList<ParamDtoItem>>> ParamDtos()
        => Ok(ApiResponse<IReadOnlyList<ParamDtoItem>>.Ok(_catalog.ParamDtos, HttpContext));

    /// <summary>
    /// Unknown <paramref name="fqdn"/> → 404 NotFound. The UI silences this path
    /// (client.ts SILENT_PATHS) so the editor falls back to free-form JSON
    /// instead of toasting per render.
    /// </summary>
    [HttpGet("param-dtos/schema")]
    public ActionResult<ApiResponse<ParamDtoSchema>> ParamDtoSchema([FromQuery] string? fqdn)
    {
        if (string.IsNullOrWhiteSpace(fqdn))
            throw new BusinessRuleException("fqdn is required");

        var schema = _catalog.GetSchema(fqdn) ?? throw new NotFoundException("ParamDto", fqdn);
        return Ok(ApiResponse<ParamDtoSchema>.Ok(schema, HttpContext));
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.Admin.Infrastructure.Auth;
using Portfolio.DiagnosisAgent.Mcp;
using Portfolio.Admin.Models;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.Admin.Controllers.Sysadmin;

/// <summary>
/// 오류 진단 MCP 개인 토큰 API(docs/DIAGNOSIS_MCP_DESIGN_2026-10.md 8장).
/// 메뉴 권한이 곧 자격이다 — 내 토큰은 진단 대화·진단 메뉴 중 하나, 전체 관리는 MCP 토큰 관리 메뉴.
/// </summary>
[ApiController]
[Route("api/admin/sysadmin")]
[Authorize]
public class DiagnosisMcpTokenController : ControllerBase
{
    private const string AdminPolicy = DynamicMenuPolicyProvider.MenuPolicyPrefix + "sysadmin.mcpTokens";

    private readonly DiagnosisMcpTokenService _tokens;

    public DiagnosisMcpTokenController(DiagnosisMcpTokenService tokens) => _tokens = tokens;

    [HttpGet("mcp-token")]
    [Authorize(Policy = DiagnosisMcpAuth.MenuPolicy)]
    public async Task<ActionResult<ApiResponse<DiagnosisMcpTokenStatusDto>>> GetMine(CancellationToken ct)
        => Ok(ApiResponse<DiagnosisMcpTokenStatusDto>.Ok(await _tokens.GetMineAsync(CurrentUserId(), ct), HttpContext));

    /// <summary>발급·재발급. 토큰 원문은 이 응답에만 실린다.</summary>
    [HttpPost("mcp-token")]
    [Authorize(Policy = DiagnosisMcpAuth.MenuPolicy)]
    public async Task<ActionResult<ApiResponse<DiagnosisMcpTokenIssuedDto>>> Issue(CancellationToken ct)
        => Ok(ApiResponse<DiagnosisMcpTokenIssuedDto>.Ok(await _tokens.IssueAsync(CurrentUserId(), ct), HttpContext));

    [HttpDelete("mcp-token")]
    [Authorize(Policy = DiagnosisMcpAuth.MenuPolicy)]
    public async Task<ActionResult<ApiResponse>> RevokeMine(CancellationToken ct)
    {
        await _tokens.RevokeAsync(CurrentUserId(), ct);
        return Ok(ApiResponse.OkMessage("MCP 토큰을 폐기했습니다.", HttpContext));
    }

    [HttpGet("mcp-tokens")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DiagnosisMcpTokenRowDto>>>> List(CancellationToken ct)
        => Ok(ApiResponse<IReadOnlyList<DiagnosisMcpTokenRowDto>>.Ok(await _tokens.ListAsync(ct), HttpContext));

    [HttpDelete("mcp-tokens/{userId}")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<ApiResponse>> Revoke(string userId, CancellationToken ct)
    {
        await _tokens.RevokeAsync(userId, ct);
        return Ok(ApiResponse.OkMessage("MCP 토큰을 폐기했습니다.", HttpContext));
    }

    private string CurrentUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? throw new BusinessRuleException("로그인 정보에 사용자 ID 가 없습니다.", StatusCodes.Status401Unauthorized);
}

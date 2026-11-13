using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Portfolio.Admin.Infrastructure.Auth;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>진단 MCP 인증·인가 상수. 발급·사용 자격 = 진단 대화 메뉴 또는 진단 메뉴 중 하나(진단 화면을 정리해도 자격이 이어지게).</summary>
public static class DiagnosisMcpAuth
{
    public const string Scheme = "DiagnosisMcpToken";

    private const string ChatMenu      = "sysadmin.diagnosisChat";
    private const string DiagnosisMenu = "sysadmin.diagnosis";

    /// <summary>토큰 API(내 토큰)용 메뉴 정책 이름 — JWT 로 로그인한 화면 요청에 쓴다.</summary>
    public const string MenuPolicy = DynamicMenuPolicyProvider.MenuPolicyPrefix + ChatMenu + "|" + DiagnosisMenu;

    /// <summary>
    /// <c>/mcp</c> 정책 — 이 스킴 하나로만 인증해 JWT(쿠키·헤더)를 타지 않게 하고, 같은 메뉴 권한을 본다.
    /// </summary>
    public static AuthorizationPolicy EndpointPolicy { get; } = new AuthorizationPolicyBuilder(Scheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new MenuPermissionRequirement(ChatMenu, DiagnosisMenu))
        .Build();
}

/// <summary>
/// <c>Authorization: Bearer wsd_...</c> 개인 토큰 인증. 요청마다 DB 의 현재 계정 상태·역할로 principal 을 만든다.
/// 클레임은 JWT 와 같은 이름(NameIdentifier=사용자 ID, Name=로그인 ID, Role)이라 메뉴 권한 판정이 그대로 돈다.
/// </summary>
public sealed class DiagnosisMcpTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string Bearer = "Bearer ";

    private readonly DiagnosisMcpTokenService _tokens;

    public DiagnosisMcpTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        DiagnosisMcpTokenService tokens)
        : base(options, logger, encoder)
        => _tokens = tokens;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var token = header[Bearer.Length..].Trim();
        if (!token.StartsWith(DiagnosisMcpTokenService.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.Fail("MCP 토큰 형식이 아닙니다.");

        var hit = await _tokens.AuthenticateAsync(
            token, DiagnosisMcpAccessGate.ClientIp(Context)?.ToString(), Context.RequestAborted);
        if (hit is null) return AuthenticateResult.Fail("유효하지 않은 MCP 토큰입니다.");

        var (user, roles) = hit.Value;
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name,           user.UserName ?? ""),
            .. roles.Select(r => new Claim(ClaimTypes.Role, r)),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

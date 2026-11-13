using System.Net.Http.Headers;
using Portfolio.Admin.Infrastructure.Auth;

namespace Portfolio.Admin.Infrastructure.Scheduler;

/// <summary>
/// Admin 백엔드 → 통합 스케줄러(내부 전용) 리버스 프록시.
/// <para>
/// 과거 브라우저가 공개 FQDN(scheduler.example.com)으로 직접 호출하던 잡 실행 / dispatch-pause /
/// 카탈로그 / deprecate 엔드포인트를, Admin 이 동일한 <c>/api/admin/*</c> 경로로 받아 내부망의
/// 스케줄러로 그대로 포워딩한다. 비즈로직은 없다 — 메서드·경로·쿼리·본문·Authorization 헤더를
/// 패스스루하고 스케줄러 응답(상태코드·Content-Type·바디)을 그대로 relay 한다.
/// 양 솔루션 모두 라우트 prefix 가 <c>/api/admin/*</c> 로 동일하므로 경로 변환이 필요 없다.
/// </para>
/// <para>
/// BaseAddress 는 <c>appsettings.json → Scheduler:InternalBaseUrl</c> (운영은 같은 서버의 내부 포트).
/// 스케줄러 컨트롤러에 [Authorize] 가 붙어 있으므로 여기서 전달하는 사용자 JWT 가 스케줄러에서 검증된다
/// (양 솔루션이 동일 Jwt 섹션을 공유).
/// </para>
/// </summary>
public sealed class SchedulerProxyClient
{
    private readonly HttpClient _http;

    public SchedulerProxyClient(HttpClient http) => _http = http;

    /// <summary>
    /// 현재 요청을 스케줄러의 동일 경로(Request.Path + QueryString)로 포워딩하고 응답을 그대로 흘려보낸다.
    /// </summary>
    public async Task ForwardAsync(HttpContext ctx)
    {
        var req    = ctx.Request;
        var target = $"{req.Path}{req.QueryString}";   // BaseAddress 기준 상대 경로

        using var msg = new HttpRequestMessage(new HttpMethod(req.Method), target);

        // 본문 포워딩 — 바디를 가질 수 있는 메서드만.
        if (HasBody(req))
        {
            var buffer = new MemoryStream();
            await req.Body.CopyToAsync(buffer, ctx.RequestAborted);
            buffer.Position = 0;
            msg.Content = new StreamContent(buffer);
            if (!string.IsNullOrEmpty(req.ContentType)
                && MediaTypeHeaderValue.TryParse(req.ContentType, out var mediaType))
            {
                msg.Content.Headers.ContentType = mediaType;
            }
        }

        // 브라우저는 HttpOnly 쿠키(admin_at)로 인증하므로 Authorization 헤더가 없다.
        // access 토큰을 쿠키에서 꺼내 스케줄러로 Bearer 로 포워딩한다(스케줄러 [Authorize] 검증 대상).
        var accessToken = req.Cookies[AuthCookies.AccessCookie];
        if (!string.IsNullOrEmpty(accessToken))
            msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

        // 프론트-백엔드 로그 상관관계 헤더도 함께 전달(있으면).
        foreach (var name in CorrelationHeaders)
            if (req.Headers.TryGetValue(name, out var value))
                msg.Headers.TryAddWithoutValidation(name, value.ToArray());

        using var resp = await _http.SendAsync(
            msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType is { } contentType)
            ctx.Response.ContentType = contentType.ToString();

        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private static readonly string[] CorrelationHeaders = { "X-Client-Trace-Id", "X-Client-Version" };

    private static bool HasBody(HttpRequest req) =>
        HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method) || HttpMethods.IsPatch(req.Method);
}

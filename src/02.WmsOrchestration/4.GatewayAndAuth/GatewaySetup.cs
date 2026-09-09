using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Portfolio.WmsOrchestration.GatewayAndAuth;

/// <summary>
/// [담당업무 4] YARP 기반 통합 라우팅과 요청량 제한.
///
/// 모놀리식 API 하나였던 진입점을 게이트웨이로 바꾸고, 뒤의 서비스(주문·재고·연동·오케스트레이션)로 라우팅한다.
/// 클라이언트는 URL 이 바뀌지 않았다 — MSA 전환이 외부에 보이지 않게 진행된 이유.
/// 요청량 제한은 테넌트(JWT 의 tenant 클레임) 단위로 건다. 한 화주의 폭주가 다른 화주를 느리게 하지 않는다.
/// 라우트·클러스터 정의는 yarp.appsettings.json — 서비스 추가는 설정.
/// </summary>
public static class GatewaySetup
{
    public static void AddGateway(this WebApplicationBuilder builder)
    {
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .AddTransforms(t => t.AddRequestHeader("X-Gateway", "wms-orchestration"));

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 테넌트별 토큰 버킷 — 인증 전 요청은 IP 로
            o.AddPolicy("per-tenant", ctx =>
            {
                var tenant = ctx.User.FindFirst("tenant")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
                return RateLimitPartition.GetTokenBucketLimiter(tenant, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 200, TokensPerPeriod = 100, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 50, AutoReplenishment = true
                });
            });

            // 대량 연동 제출은 더 엄격하게
            o.AddPolicy("bulk-submit", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirst("tenant")?.Value ?? "anon",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    public static void UseGateway(this WebApplication app)
    {
        app.UseRateLimiter();
        app.MapReverseProxy(proxy =>
        {
            proxy.UseRateLimiter();       // 라우트별 정책은 yarp.appsettings.json 의 RateLimiterPolicy
        }).RequireAuthorization("tenant-scoped");
        app.MapHub<StatusHub>("/hubs/status").RequireAuthorization("operator");
    }
}

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Portfolio.WmsOrchestration.FaultTolerance;

namespace Portfolio.WmsOrchestration.GatewayAndAuth;

/// <summary>
/// [담당업무 4] 이원화 DB 구성.
///
///   PostgreSQL : 오케스트레이션 자체 상태 — Saga · Outbox/Inbox · 실행 이력. 쓰기 많고 행 단위 락이 중요.
///   SQL Server : 자사 WMS 원장 — 주문·재고·마스터. 기존 시스템과 프로시저를 그대로 쓴다 (Dapper).
/// 오케스트레이션이 WMS 원장 DB 에 자기 테이블을 만들지 않는다 — 장애 반경과 스키마 소유권을 분리.
/// </summary>
public static class DualDatabaseSetup
{
    public static void AddDualDatabases(this IServiceCollection services, IConfiguration cfg)
    {
        // 오케스트레이션 상태 (PostgreSQL)
        services.AddDbContext<OrchestrationDbContext>(o => o
            .UseNpgsql(cfg.GetConnectionString("Orchestration"), n => n.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention());

        // WMS 원장 (SQL Server) — 읽기 위주, Dapper 연결로 제공
        services.AddScoped<IWmsConnectionFactory>(_ => new WmsConnectionFactory(cfg.GetConnectionString("Wms")!));
    }
}

public interface IWmsConnectionFactory { System.Data.IDbConnection Open(); }
public sealed class WmsConnectionFactory : IWmsConnectionFactory
{
    private readonly string _cs;
    public WmsConnectionFactory(string cs) => _cs = cs;
    public System.Data.IDbConnection Open() { var c = new Microsoft.Data.SqlClient.SqlConnection(_cs); c.Open(); return c; }
}

/// <summary>
/// [담당업무 4] JWT · 다중 테넌트 접근 제어.
///
/// 토큰의 tenant 클레임이 곧 데이터 경계다. 게이트웨이(YARP)는 "tenant-scoped" 정책으로 토큰 유무·유효성을 보고,
/// 각 서비스는 <see cref="TenantContext"/> 로 현재 테넌트를 받아 쿼리마다 tenant 필터를 강제한다.
/// 운영자 역할은 여러 테넌트를 볼 수 있지만, 그것도 클레임(tenants: ["A","B"])으로 명시된 범위만.
/// </summary>
public static class JwtMultiTenantAuth
{
    public static void AddJwtMultiTenant(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = cfg["Jwt:Issuer"], ValidAudience = cfg["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)),
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
            };
            o.Events = new JwtBearerEvents
            {
                // SignalR 은 헤더 대신 쿼리스트링으로 토큰을 보낸다
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var t)) ctx.Token = t;
                    return Task.CompletedTask;
                }
            };
        });

        services.AddAuthorization(o =>
        {
            o.AddPolicy("tenant-scoped", p => p.RequireAuthenticatedUser().RequireClaim("tenant"));
            o.AddPolicy("operator", p => p.RequireRole("Operator"));
        });

        services.AddScoped<TenantContext>();
    }
}

/// <summary>요청 스코프의 현재 테넌트. 리포지토리는 이 값 없이 쿼리할 수 없다.</summary>
public sealed class TenantContext
{
    public string TenantId { get; }
    public IReadOnlySet<string> VisibleTenants { get; }
    public bool IsOperator { get; }

    public TenantContext(IHttpContextAccessor http)
    {
        var user = http.HttpContext?.User ?? throw new UnauthorizedAccessException();
        TenantId = user.FindFirst("tenant")?.Value ?? throw new UnauthorizedAccessException("tenant 클레임 없음");
        IsOperator = user.IsInRole("Operator");
        VisibleTenants = user.FindAll("tenants").Select(c => c.Value).Append(TenantId).ToHashSet();
    }

    /// <summary>다른 테넌트의 자원 접근 시도를 요청 단계에서 끊는다.</summary>
    public void EnsureCanAccess(string tenantId)
    {
        if (!VisibleTenants.Contains(tenantId)) throw new UnauthorizedAccessException($"테넌트 {tenantId} 접근 불가");
    }
}

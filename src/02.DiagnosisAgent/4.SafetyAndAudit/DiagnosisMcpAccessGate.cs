using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>
/// <c>/mcp</c> 접근 검사 — HTTPS 가 아니거나(<see cref="DiagnosisMcpOptions.RequireHttps"/> 일 때) 허용 IP·대역이 아니면
/// 토큰을 보기 전에 403 으로 끊어, 회사 밖 요청이 DB 조회까지 가지 않게 한다.
/// IP 는 RemoteIpAddress 그대로다 — 앞단에 프록시·로드밸런서가 생기면 rate limiter 와 함께 ForwardedHeaders 설정이 필요하다.
/// 허용 목록이 비었으면 모두 막고, 형식이 틀리면 기동 때 실패한다.
/// </summary>
public sealed class DiagnosisMcpAccessGate
{
    public const string Path = "/mcp";

    private readonly RequestDelegate                 _next;
    private readonly bool                            _requireHttps;
    private readonly List<IPNetwork>                 _allowed;
    private readonly ILogger<DiagnosisMcpAccessGate> _logger;

    public DiagnosisMcpAccessGate(
        RequestDelegate next, IOptions<DiagnosisOptions> options, ILogger<DiagnosisMcpAccessGate> logger)
    {
        _next         = next;
        _logger       = logger;
        _requireHttps = options.Value.Mcp.RequireHttps;
        _allowed      = options.Value.Mcp.AllowedIps.Select(ParseAllowed).ToList();
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        var ip = ClientIp(ctx);
        if ((ctx.Request.IsHttps || !_requireHttps) && ip is not null && _allowed.Any(n => n.Contains(ip)))
            return _next(ctx);

        _logger.LogWarning(AppLog.Log("[DiagnosisMcp] 접근 거절 ip={Ip} https={Https}"), ip, ctx.Request.IsHttps);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>요청 IP. IPv4 를 IPv6 로 감싼 주소(::ffff:1.2.3.4)는 IPv4 로 푼다 — 허용 목록은 IPv4 로 적는다.</summary>
    public static IPAddress? ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
    }

    /// <summary>허용 항목 — 대역(<c>10.0.0.0/24</c>)은 그대로, 주소 하나는 그 주소만 담은 대역(/32, IPv6 는 /128)으로.</summary>
    private static IPNetwork ParseAllowed(string entry)
    {
        if (entry.Contains('/')) return IPNetwork.Parse(entry);
        var ip = IPAddress.Parse(entry);
        return new IPNetwork(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }
}

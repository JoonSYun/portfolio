using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Portfolio.DiagnosisAgent;
using Portfolio.DiagnosisAgent.Chat;
using Portfolio.DiagnosisAgent.Mcp;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.Admin.Infrastructure;

/// <summary>
/// WMS 오류 진단 에이전트 DI 등록(docs/DIAGNOSIS_AGENT_DESIGN_2026-10.md).
/// <c>SqlIdentifierValidator</c> 는 SystemMonitor 등록분을 같이 쓴다.
/// </summary>
public static class DiagnosisServicesExtensions
{
    public static IServiceCollection AddDiagnosisServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DiagnosisOptions>(configuration.GetSection("Diagnosis"));

        // 소스 복사본 + GitHub 동기화(저장소 목록을 비우면 소유자 저장소 전체)
        services.AddSingleton(sp =>
            new SourceMirror(sp.GetRequiredService<IOptions<DiagnosisOptions>>().Value.SourceRoot));
        services.AddHttpClient(SourceMirrorService.HttpClientName, (sp, client) =>
        {
            var gh = sp.GetRequiredService<IOptions<DiagnosisOptions>>().Value.GitHub;
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout     = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Portfolio.Admin-Diagnosis");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(gh.Token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gh.Token);
        });
        services.AddHostedService<SourceMirrorService>();

        // DB 도구 — 무상태 singleton
        services.AddSingleton<ReadOnlyBatchValidator>();
        services.AddSingleton<DiagnosisDbTools>();

        // 로그 도구 — 이 서버의 Seq(Diagnosis:Seq). 주소·키는 요청마다 붙인다.
        services.AddHttpClient(DiagnosisSeqTools.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(DiagnosisSeqTools.TimeoutSeconds));
        services.AddSingleton<DiagnosisSeqTools>();

        // 지식 문서 — 출력 폴더의 md 를 처음 쓸 때 읽는다(없으면 그 자리에서 실패)
        services.AddSingleton(_ => KnowledgeBase.Load(
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Diagnosis", "Knowledge")));

        // 에이전트·실행
        services.AddSingleton<DiagnosisAgent>();
        services.AddSingleton<DiagnosisRunStore>();
        services.AddSingleton<DiagnosisService>();

        // 대화 화면(docs/DIAGNOSIS_CHAT_DESIGN_2026-10.md) — 기존 화면과 따로 돈다(동시 실행 슬롯도 따로).
        services.AddSingleton(_ => new DiagnosisChatKnowledge(KnowledgeBase.LoadAll(
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Diagnosis", "Knowledge"),
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Diagnosis", "Knowledge", "Chat"))));
        services.AddSingleton<DiagnosisConversationRepository>();
        services.AddSingleton<DiagnosisChatLiveTurns>();
        services.AddSingleton<DiagnosisChatAgent>();
        services.AddSingleton<DiagnosisChatService>();

        // MCP 공개(docs/DIAGNOSIS_MCP_DESIGN_2026-10.md) — 조회 도구·안내·진단 시작 프롬프트를 개인 토큰으로 연다.
        // 판단은 접속한 Claude 세션이 하고 서버는 도구만 실행한다. 요청마다 독립(stateless) — 세션을 들고 있지 않는다.
        services.AddScoped<DiagnosisMcpTokenService>();
        services.AddScoped<DiagnosisMcpRequestService>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DiagnosisMcpTokenHandler>(DiagnosisMcpAuth.Scheme, _ => { });
        services.AddMcpServer(o =>
            {
                o.ServerInfo         = new Implementation { Name = "wms-diag", Version = "1.0.0" };
                o.ServerInstructions = DiagnosisMcpGuide.ServerInstructions;
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<DiagnosisMcpTools>()
            .WithPrompts<DiagnosisMcpPrompts>()
            // 호출 기록(docs/DIAGNOSIS_MCP_REQUEST_DESIGN_2026-11.md) — 도구·프롬프트 호출마다 DiagnosisMcpCall 1행.
            .WithRequestFilters(f =>
            {
                f.AddCallToolFilter(DiagnosisMcpCallAudit.Tools);
                f.AddGetPromptFilter(DiagnosisMcpCallAudit.Prompts);
            });

        return services;
    }

    /// <summary>
    /// <c>/mcp</c> 접근 검사(HTTPS·허용 IP). 토큰 인증보다 먼저 막아야 하므로
    /// <see cref="AdminAuthExtensions.UseAdminAuth"/> 앞에서 부른다.
    /// </summary>
    public static WebApplication UseDiagnosisMcpGate(this WebApplication app)
    {
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(DiagnosisMcpAccessGate.Path),
            branch => branch.UseMiddleware<DiagnosisMcpAccessGate>());
        return app;
    }

    /// <summary>MCP 엔드포인트. 정책 하나로 개인 토큰 인증과 메뉴 권한을 함께 본다.</summary>
    public static WebApplication MapDiagnosisMcp(this WebApplication app)
    {
        app.MapMcp(DiagnosisMcpAccessGate.Path).RequireAuthorization(DiagnosisMcpAuth.EndpointPolicy);
        return app;
    }
}

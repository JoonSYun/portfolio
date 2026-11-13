using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>
/// 진단 시작 프롬프트(docs/DIAGNOSIS_MCP_DESIGN_2026-10.md 7장). Claude Code 에서 <c>/wms-diag:diagnose [test]</c> 로 보인다.
/// 결과에 안내(지식 문서)가 들어가므로 이 프롬프트로 시작하면 read_guide 를 따로 부르지 않는다.
/// </summary>
[McpServerPromptType]
public sealed class DiagnosisMcpPrompts
{
    private readonly SourceMirror                 _mirror;
    private readonly KnowledgeBase                _knowledge;
    private readonly IHttpContextAccessor         _http;
    private readonly ILogger<DiagnosisMcpPrompts> _logger;

    public DiagnosisMcpPrompts(
        SourceMirror mirror, KnowledgeBase knowledge, IHttpContextAccessor http, ILogger<DiagnosisMcpPrompts> logger)
    {
        _mirror    = mirror;
        _knowledge = knowledge;
        _http      = http;
        _logger    = logger;
    }

    [McpServerPrompt(Name = "diagnose"), Description(DiagnosisMcpGuide.DiagnosePromptDescription)]
    public string Diagnose([Description(DiagnosisToolTexts.EnvArg)] string? env = null)
    {
        var target = DiagnosisMcpTools.ResolveEnv(env);
        _logger.LogInformation(AppLog.Log("[DiagnosisMcp] user={User} prompt=diagnose env={Env}"),
            _http.HttpContext?.User.FindFirstValue(ClaimTypes.Name), target);
        return DiagnosisMcpGuide.DiagnoseMessage(_knowledge, _mirror, target);
    }
}

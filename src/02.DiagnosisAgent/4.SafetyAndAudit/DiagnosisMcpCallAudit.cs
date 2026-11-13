using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>
/// MCP 호출 기록 필터(docs/DIAGNOSIS_MCP_REQUEST_DESIGN_2026-11.md) — 도구·프롬프트 호출을 한 곳에서 감싸
/// <c>DiagnosisMcpCall</c> 에 1행씩 남긴다. 받은 인자 원본·클라이언트 호출 번호(_meta)·소요 시간·결과 구분을 남기고, 결과 본문은 크기만 남긴다.
/// <para>
/// SDK(ModelContextProtocol 2.2.0)는 도구 예외를 이 필터 바깥에서 오류 결과로 바꾼다. 그래서 여기서는 예외를 그대로 받아
/// 결과를 가르고(<see cref="Classify"/>) 다시 던진다. 없는 도구·인자 바인딩 실패도 이 필터를 지나므로 같이 남는다.
/// </para>
/// <para>
/// 기록 실패는 경고 로그만 남기고 호출 결과를 막지 않는다(저장소 감사 기록 규약). <c>/mcp</c> 는 HTTP 요청 안에서만 돌아
/// HttpContext 가 늘 있다(stateless).
/// </para>
/// </summary>
public static class DiagnosisMcpCallAudit
{
    /// <summary>Claude Code 가 tools/call 의 _meta 에 싣는 호출 번호(tool_use id).</summary>
    private const string ToolCallIdMetaKey = "claudecode/toolUseId";

    /// <summary>대상 환경을 갖는 호출 — DB 도구와 진단 시작 프롬프트. 인자 env 를 생략하면 운영이다.</summary>
    private static readonly IReadOnlySet<string> EnvTargets = new HashSet<string>(StringComparer.Ordinal)
    {
        "search_db_objects", "read_db_object", "run_query", "diagnose",
    };

    /// <summary>저장·검색용이라 한글을 \uXXXX 로 바꾸지 않는다(HTML 에 넣지 않는다).</summary>
    private static readonly JsonSerializerOptions ArgumentsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Tools(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => (context, ct) => AuditAsync(
            context, DiagnosisMcpMethods.ToolsCall, context.Params?.Name, context.Params?.Arguments, context.Params?.Meta,
            () => next(context, ct), r => (r.IsError == true, r.Content), ct);

    public static McpRequestHandler<GetPromptRequestParams, GetPromptResult> Prompts(
        McpRequestHandler<GetPromptRequestParams, GetPromptResult> next)
        => (context, ct) => AuditAsync(
            context, DiagnosisMcpMethods.PromptsGet, context.Params?.Name, context.Params?.Arguments, context.Params?.Meta,
            () => next(context, ct), r => (false, r.Messages.Select(m => m.Content)), ct);

    private static async ValueTask<TResult> AuditAsync<TResult>(
        MessageContext context, string method, string? name, IDictionary<string, JsonElement>? args, JsonObject? meta,
        Func<ValueTask<TResult>> run, Func<TResult, (bool IsError, IEnumerable<ContentBlock> Content)> inspect, CancellationToken ct)
    {
        var http = (context.Services ?? context.Server.Services)!.GetRequiredService<IHttpContextAccessor>().HttpContext!;
        DiagnosisMcpCallEntity call;
        try
        {
            call = Begin(http, method, name, args, meta);
        }
        catch (Exception ex)
        {
            Logger(http).LogWarning(ex, AppLog.Log("[DiagnosisMcp] 호출 기록 준비 실패 method={Method} name={Name}"), method, name);
            return await run();
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await run();
            var (isError, content) = inspect(result);
            var texts = content.OfType<TextContentBlock>().Select(b => b.Text).ToList();
            call.Outcome      = isError ? DiagnosisMcpOutcomes.Failed : DiagnosisMcpOutcomes.Success;
            call.ErrorMessage = isError ? Cut(SecretMasker.Mask(string.Join("\n", texts)), 2000) : null;
            call.ResponseSize = texts.Sum(Encoding.UTF8.GetByteCount);
            return result;
        }
        catch (Exception ex)
        {
            call.Outcome      = Classify(ex, ct.IsCancellationRequested);
            call.ErrorMessage = Cut(SecretMasker.Mask(ex.Message), 2000);
            throw;
        }
        finally
        {
            call.DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            try
            {
                await http.RequestServices.GetRequiredService<DiagnosisMcpRequestService>().RecordCallAsync(call);
            }
            catch (Exception ex)
            {
                Logger(http).LogWarning(ex, AppLog.Log("[DiagnosisMcp] 호출 기록 실패 name={Name} request={McpRequestId}"), call.Name, call.RequestId);
            }
        }
    }

    /// <summary>
    /// 예외 → 결과 구분. 도구 안에서 난 예외는 <c>DiagnosisMcpTools.RunAsync</c> 가 McpException 으로 감싸므로 안쪽 예외로 가른다.
    /// 감싸지 않은 예외는 도구 밖(요청 ID·env 검사, 없는 도구, SDK 인자 바인딩)에서 난 것이다.
    /// </summary>
    internal static string Classify(Exception ex, bool cancelled)
    {
        if (ex is OperationCanceledException && cancelled) return DiagnosisMcpOutcomes.Cancelled;
        if (ex is McpException { InnerException: { } inner })
            return inner switch
            {
                DiagnosisToolFailureException => DiagnosisMcpOutcomes.Failed,
                DiagnosisToolException        => DiagnosisMcpOutcomes.Rejected,
                _                             => DiagnosisMcpOutcomes.Failed,
            };
        return ex switch
        {
            // 직접 던진 거절(요청 ID 없음·env 오류)과 없는 도구(McpProtocolException 도 McpException 이다)
            McpException                       => DiagnosisMcpOutcomes.Rejected,
            // SDK 인자 바인딩 실패(필수 인자 누락·형식 오류)
            ArgumentException or JsonException => DiagnosisMcpOutcomes.Rejected,
            _                                  => DiagnosisMcpOutcomes.Failed,
        };
    }

    private static DiagnosisMcpCallEntity Begin(
        HttpContext http, string method, string? name, IDictionary<string, JsonElement>? args, JsonObject? meta)
    {
        var tool = name ?? "";
        return new DiagnosisMcpCallEntity
        {
            RequestId         = Cut(Arg(args, "request_id"), 12),
            UserId            = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            UserEmail         = http.User.FindFirstValue(ClaimTypes.Name) ?? "",
            ClientIp          = DiagnosisMcpAccessGate.ClientIp(http)?.ToString() ?? "",
            Method            = method,
            Name              = Cut(tool, 64)!,
            TargetEnvironment = EnvTargets.Contains(tool) ? Cut(Env(Arg(args, "env")), 10) : null,
            TargetServer      = Cut(Arg(args, "server"), 10),
            TargetDatabase    = Cut(Arg(args, "database"), 128),
            Arguments         = ArgumentsText(args),
            StartedAt         = DatetimeHelper.Now,
            ToolCallId        = Cut(meta?[ToolCallIdMetaKey] is JsonValue v && v.TryGetValue(out string? id) ? id : null, 100),
            TraceId           = Cut(Activity.Current?.TraceId.ToHexString() ?? http.TraceIdentifier, 64),
        };
    }

    /// <summary>
    /// 받은 인자 전체 JSON(비밀값 가림). 가림은 글자 단위라 값 안의 이스케이프(\")를 끊으면 JSON 이 깨질 수 있다
    /// (Password=ab\"cd → 따옴표가 풀림). 그때는 가린 글을 문자열 하나({"masked": "…"})로 감싸 DB 의 ISJSON 검사를 통과시킨다.
    /// </summary>
    internal static string ArgumentsText(IDictionary<string, JsonElement>? args)
    {
        var masked = SecretMasker.Mask(JsonSerializer.Serialize(args ?? new Dictionary<string, JsonElement>(), ArgumentsJson));
        try
        {
            using var _ = JsonDocument.Parse(masked);
            return masked;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { masked }, ArgumentsJson);
        }
    }

    /// <summary>인자 하나를 글자로. 문자열은 값 그대로, 그 밖 형식은 JSON 원문, 없거나 null 이면 null.</summary>
    private static string? Arg(IDictionary<string, JsonElement>? args, string key)
        => args is not null && args.TryGetValue(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String                         => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _                                             => v.GetRawText(),
            }
            : null;

    /// <summary>env 인자 → 대상 환경(생략하면 prod). 틀린 값은 받은 그대로 남긴다(그 호출은 거절된다).</summary>
    private static string Env(string? raw)
    {
        try
        {
            return DiagnosisMcpTools.ResolveEnv(raw);
        }
        catch (McpException)
        {
            return raw ?? "";
        }
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    private static ILogger Logger(HttpContext http)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DiagnosisMcpCallAudit));
}

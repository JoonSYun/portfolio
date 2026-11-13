using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>
/// MCP 로 여는 조회 도구 7개(소스 3·DB 3·로그 1) + 안내 읽기(docs/DIAGNOSIS_MCP_DESIGN_2026-10.md 6장)
/// + 요청 시작 도구(docs/DIAGNOSIS_MCP_REQUEST_DESIGN_2026-11.md).
/// 진단 에이전트와 같은 소스 복사본·진단 DB 계정·Seq·지식 문서를 쓰고 설명 문구도 같은 상수(<see cref="DiagnosisToolTexts"/>)를 쓴다.
/// 판단은 접속한 Claude 세션이 한다 — 호출 상한·범위 거절은 없고, 결과 길이 상한과 DB 도구 상한은 그대로다.
/// 조회 도구는 본인 요청 ID 가 있어야 돈다(<see cref="LookupAsync"/>). 호출 기록은 필터(<see cref="DiagnosisMcpCallAudit"/>)가 한 곳에서 남기고,
/// 사용자 질문·답변 내용은 받지도 저장하지도 않는다.
/// 매개변수 이름이 곧 도구 인자 이름이라 에이전트 도구와 같게 snake_case 로 둔다.
/// </summary>
/// <remarks>
/// <para>속성별 역할(ModelContextProtocol SDK 2.2.0 기준).</para>
/// <para>
/// <c>[McpServerToolType]</c>: 도구 메서드를 담은 클래스라는 표시다. <c>WithToolsFromAssembly</c> 처럼 어셈블리를 훑어 등록할 때 찾는 표식이다.
/// 여기서는 <c>WithTools&lt;DiagnosisMcpTools&gt;()</c> 로 타입을 직접 등록하므로(<c>DiagnosisServicesExtensions</c>) 동작에는 필요 없고 표시로만 둔다.
/// 도구가 인스턴스 메서드라 부를 때마다 이 클래스를 새로 만들고, 생성자 매개변수는 그 요청의 DI 컨테이너에서 받는다.
/// </para>
/// <para>
/// <c>[McpServerTool(Name = …, ReadOnly = true)]</c>: 이 메서드를 MCP 도구로 연다.
/// 이 속성이 붙은 메서드만 도구가 되고, <see cref="ResolveEnv"/>·<c>RunAsync</c> 처럼 속성이 없는 메서드는 공개되지 않는다.
/// </para>
/// <para>
/// Name 은 클라이언트가 도구 목록(tools/list)에서 보고 호출(tools/call)할 때 쓰는 도구 이름이다.
/// 생략하면 SDK 가 메서드 이름으로 만든다. 에이전트 도구와 같은 이름이어야 해서 직접 적는다.
/// </para>
/// <para>
/// ReadOnly 는 도구 주석(annotations)의 readOnlyHint 다. "환경을 바꾸지 않는 도구"라고 클라이언트에 알리는 힌트일 뿐 서버에서 막는 장치가 아니다.
/// 실제 읽기 전용은 진단 DB 계정 권한과 <see cref="ReadOnlyBatchValidator"/> 가 지킨다.
/// 지정하지 않은 힌트는 MCP 기본값으로 해석된다(Destructive=true, Idempotent=false, OpenWorld=true).
/// Destructive·Idempotent 는 ReadOnly=false 일 때만 의미가 있다.
/// </para>
/// <para>
/// <c>[Description]</c>(System.ComponentModel): 메서드에 붙이면 도구 설명, 매개변수에 붙이면 입력 스키마(inputSchema)의 그 인자 설명이 된다.
/// 둘 다 모델에 그대로 보이고, 모델은 이 문구를 보고 어느 도구를 어떤 값으로 부를지 정한다.
/// 속성 인자는 컴파일 시 상수여야 해서 <see cref="DiagnosisToolTexts"/> 의 const 문자열을 쓴다.
/// <c>[McpServerTool(…), Description(…)]</c> 처럼 한 대괄호에 쉼표로 이어 쓴 것은 속성 두 개를 단 것이다.
/// </para>
/// <para>
/// 매개변수: 이름이 곧 인자 이름이고 C# 타입이 스키마 타입이 된다(string → string, int? → integer, bool → boolean, string[] → array).
/// 기본값(<c>= null</c> 등)이 있으면 선택 인자, 없으면 필수 인자다.
/// <see cref="CancellationToken"/> 은 스키마에 들어가지 않는다. SDK 가 넘겨주고, 클라이언트가 호출을 취소하면 함께 취소된다.
/// </para>
/// <para>
/// 반환값·예외: string 반환값은 텍스트 결과 하나로 나간다.
/// 예외는 <see cref="McpException"/> 의 메시지만 클라이언트에 보이고 다른 예외는 SDK 가 일반 오류 문구로 바꾼다. 그래서 <c>RunAsync</c> 가 McpException 으로 감싼다.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class DiagnosisMcpTools
{
    private readonly SourceMirror               _mirror;
    private readonly DiagnosisDbTools           _db;
    private readonly DiagnosisSeqTools          _seq;
    private readonly KnowledgeBase              _knowledge;
    private readonly DiagnosisMcpRequestService _requests;
    private readonly IHttpContextAccessor       _http;
    private readonly ILogger<DiagnosisMcpTools> _logger;

    public DiagnosisMcpTools(
        SourceMirror mirror, DiagnosisDbTools db, DiagnosisSeqTools seq, KnowledgeBase knowledge,
        DiagnosisMcpRequestService requests, IHttpContextAccessor http, ILogger<DiagnosisMcpTools> logger)
    {
        _mirror    = mirror;
        _db        = db;
        _seq       = seq;
        _knowledge = knowledge;
        _requests  = requests;
        _http      = http;
        _logger    = logger;
    }

    /// <summary>요청 HTTP 문맥 — <c>/mcp</c> 는 인증 뒤 HTTP 요청 안에서만 돌므로(stateless) 늘 있다.</summary>
    private HttpContext Http => _http.HttpContext!;

    /// <summary>요청자 Id·이메일(토큰 소유자).</summary>
    private string UserId    => Http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private string UserEmail => Http.User.FindFirstValue(ClaimTypes.Name)!;

    // ── 요청 시작 ─────────────────────────────────────────────

    [McpServerTool(Name = "start_request", Destructive = false, OpenWorld = false), Description(DiagnosisMcpGuide.StartRequestDescription)]
    public Task<string> StartRequest(CancellationToken ct = default)
    {
        var id = DiagnosisMcpRequestService.NewId();
        return RunAsync("start_request", null, "요청 시작", null, id, async () =>
        {
            await _requests.StartAsync(new DiagnosisMcpRequestEntity
            {
                Id        = id,
                UserId    = UserId,
                UserEmail = UserEmail,
                ClientIp  = DiagnosisMcpAccessGate.ClientIp(Http)?.ToString() ?? "",
                UserAgent = Http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 512)] : null,
                CreatedAt = DatetimeHelper.Now,
            }, ct);
            return DiagnosisMcpGuide.StartResult(id);
        });
    }

    // ── 소스 ──────────────────────────────────────────────────

    [McpServerTool(Name = "find_files", ReadOnly = true), Description(DiagnosisToolTexts.FindFiles)]
    public Task<string> FindFiles(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.NamePatternArg)] string name_pattern,
        [Description(DiagnosisToolTexts.RepoArg)] string? repo = null,
        CancellationToken ct = default)
        => LookupAsync(request_id,"find_files", null, $"파일 찾기: {name_pattern}", null,
            () => Task.FromResult(_mirror.FindFiles(name_pattern, repo)), ct);

    [McpServerTool(Name = "search_code", ReadOnly = true), Description(DiagnosisToolTexts.SearchCode)]
    public Task<string> SearchCode(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.PatternArg)] string pattern,
        [Description(DiagnosisToolTexts.RepoArg)] string? repo = null,
        [Description(DiagnosisToolTexts.PathGlobArg)] string? path_glob = null,
        [Description(DiagnosisToolTexts.IgnoreCaseArg)] bool ignore_case = false,
        CancellationToken ct = default)
        => LookupAsync(request_id,"search_code", null, $"코드 검색: {pattern}", null,
            () => Task.FromResult(_mirror.Search(pattern, repo, path_glob, ignore_case)), ct);

    [McpServerTool(Name = "read_file", ReadOnly = true), Description(DiagnosisToolTexts.ReadFile)]
    public Task<string> ReadFile(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.ReadRepoArg)] string repo,
        [Description(DiagnosisToolTexts.PathArg)] string path,
        [Description(DiagnosisToolTexts.StartLineArg)] int? start_line = null,
        [Description(DiagnosisToolTexts.EndLineArg)] int? end_line = null,
        CancellationToken ct = default)
        => LookupAsync(request_id,"read_file", null, $"파일 읽기: {repo}/{path} {start_line ?? 1}줄~", null,
            () => Task.FromResult(_mirror.ReadFile(repo, path, start_line ?? 1, end_line)), ct);

    // ── DB ────────────────────────────────────────────────────

    [McpServerTool(Name = "search_db_objects", ReadOnly = true), Description(DiagnosisToolTexts.SearchDbObjects)]
    public Task<string> SearchDbObjects(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.ServerArg)] string server,
        [Description(DiagnosisToolTexts.KeywordArg)] string keyword,
        [Description(DiagnosisToolTexts.DatabaseAllArg)] string? database = null,
        [Description(DiagnosisToolTexts.TypesArg)] string[]? types = null,
        [Description(DiagnosisToolTexts.EnvArg)] string? env = null,
        CancellationToken ct = default)
    {
        var target = ResolveEnv(env);
        return LookupAsync(request_id,"search_db_objects", target, $"DB 객체 검색: {server}/{database ?? "전체"} '{keyword}'", null,
            () => _db.SearchObjectsAsync(target, server, database, keyword, types, ct), ct);
    }

    [McpServerTool(Name = "read_db_object", ReadOnly = true), Description(DiagnosisToolTexts.ReadDbObject)]
    public Task<string> ReadDbObject(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.ServerArg)] string server,
        [Description(DiagnosisToolTexts.DatabaseArg)] string database,
        [Description(DiagnosisToolTexts.ObjectNameArg)] string name,
        [Description(DiagnosisToolTexts.StartLineArg)] int? start_line = null,
        [Description(DiagnosisToolTexts.EndLineArg)] int? end_line = null,
        [Description(DiagnosisToolTexts.EnvArg)] string? env = null,
        CancellationToken ct = default)
    {
        var target = ResolveEnv(env);
        return LookupAsync(request_id,"read_db_object", target, $"DB 객체 읽기: {server}/{database}.{name}", null,
            () => _db.ReadObjectAsync(target, server, database, name, start_line ?? 1, end_line, ct), ct);
    }

    [McpServerTool(Name = "run_query", ReadOnly = true), Description(DiagnosisToolTexts.RunQuery)]
    public Task<string> RunQuery(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.ServerArg)] string server,
        [Description(DiagnosisToolTexts.QueryDatabaseArg)] string database,
        [Description(DiagnosisToolTexts.SqlArg)] string sql,
        [Description(DiagnosisToolTexts.EnvArg)] string? env = null,
        CancellationToken ct = default)
    {
        var target = ResolveEnv(env);
        return LookupAsync(request_id,"run_query", target, $"조회 실행: {server}/{database}", sql,
            () => _db.RunQueryAsync(target, server, database, sql, ct), ct);
    }

    // ── 로그 ──────────────────────────────────────────────────

    /// <summary>env 가 없다 — 이 관리자 서버의 Seq 만 본다(<see cref="DiagnosisSeqTools"/>).</summary>
    [McpServerTool(Name = "search_logs", ReadOnly = true), Description(DiagnosisToolTexts.SearchLogs)]
    public Task<string> SearchLogs(
        [Description(DiagnosisToolTexts.RequestIdArg)] string request_id,
        [Description(DiagnosisToolTexts.LogFilterArg)] string? filter = null,
        [Description(DiagnosisToolTexts.LogFromArg)] string? from = null,
        [Description(DiagnosisToolTexts.LogToArg)] string? to = null,
        [Description(DiagnosisToolTexts.LogCountArg)] int? count = null,
        CancellationToken ct = default)
        => LookupAsync(request_id,"search_logs", null, $"로그 검색: {filter ?? "전체"} ({from ?? "24시간 전"} ~ {to ?? "지금"})", null,
            () => _seq.SearchAsync(filter, from, to, count, ct), ct);

    // ── 안내 ──────────────────────────────────────────────────

    [McpServerTool(Name = "read_guide", ReadOnly = true), Description(DiagnosisMcpGuide.ReadGuideDescription)]
    public Task<string> ReadGuide()
        => RunAsync("read_guide", null, "안내 읽기", null, null,
            () => Task.FromResult(DiagnosisMcpGuide.Build(_knowledge, _mirror)));

    /// <summary>env 인자 → 대상 환경. 생략하면 운영. <see cref="DiagnosisDbTools"/> 는 검사를 마친 값을 받는다고 전제하므로 여기서 거른다.</summary>
    public static string ResolveEnv(string? env) => env?.Trim().ToLowerInvariant() switch
    {
        null or "" or DiagnosisEnvs.Prod => DiagnosisEnvs.Prod,
        DiagnosisEnvs.Test               => DiagnosisEnvs.Test,
        _                                => throw new McpException("env 는 prod(운영) 또는 test(테스트)입니다."),
    };

    /// <summary>조회 도구: 본인 요청 ID 확인 → 실행 → 결과 길이 제한 → 데이터 경계 표시.</summary>
    private Task<string> LookupAsync(
        string requestId, string tool, string? env, string summary, string? detail, Func<Task<string>> body, CancellationToken ct)
        => RunAsync(tool, env, summary, detail, requestId, async () =>
        {
            if (!await _requests.IsOwnedAsync(requestId, UserId, ct))
                throw new McpException($"요청 ID '{requestId}' 가 없습니다. 먼저 start_request 를 호출해 요청 ID 를 받으세요.");
            return DiagnosisTextFormat.MarkAsData(DiagnosisTextFormat.Truncate(await body()));
        });

    /// <summary>
    /// Seq 로그 → 실행. run_query 는 SQL 전문을 상세로 남긴다(에이전트의 도구 로그와 같은 방식).
    /// DB 호출 기록은 <see cref="DiagnosisMcpCallAudit"/> 가 남긴다. 예외를 McpException 으로 감쌀 때 원래 예외를 안쪽에 두어
    /// 호출 기록이 거절(REJECTED)·실패(FAILED)를 가를 수 있게 한다.
    /// 속성 이름 McpRequestId — ASP.NET 이 로그 범위에 넣는 RequestId(HTTP 요청 번호)와 겹치지 않게.
    /// </summary>
    private async Task<string> RunAsync(
        string tool, string? env, string summary, string? detail, string? requestId, Func<Task<string>> body)
    {
        _logger.LogInformation(AppLog.Log("[DiagnosisMcp] user={User} tool={Tool} env={Env} request={McpRequestId} {Summary} {Detail}"),
            UserEmail, tool, env, requestId, summary, detail);
        try
        {
            return await body();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or McpException))
        {
            // SDK 는 McpException 이 아닌 예외의 메시지를 숨긴다 — 에이전트처럼 모델이 오류를 읽고 고쳐 부르도록 메시지를 싣는다.
            throw new McpException(ex.Message, ex);
        }
    }
}

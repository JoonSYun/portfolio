using System.Text.Json;
using Anthropic.Helpers.Beta;
using Anthropic.Models.Beta.Messages;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 실행 1건에 쓰는 도구 묶음. 조회 도구는 모두 <see cref="GuardedAsync"/> 를 거쳐 호출 수 상한·진행 기록·결과 길이 상한을 받는다.
/// 제출 도구는 실행 종류에 따라 하나만 등록한다(진단=submit_report, 수정 방안=submit_fix).
/// 진단 실행에는 진단 범위 밖 요청을 돌려보내는 decline_request 를 함께 등록한다 — 조회 도구를 쓰기 전에만 받는다.
/// 대화 화면 턴은 <see cref="ForChat"/> 로 만든다 — 도구 정의가 대화 내내 같아야 해서 제출 도구 셋을 늘 등록하고,
/// 턴 종류에 따라 받는 제출과 안내 문구만 바꾼다.
/// 조회 도구 설명 문구는 <see cref="DiagnosisToolTexts"/> 에 두고 MCP 와 같이 쓴다.
/// <para>
/// 제출 내용은 에이전트가 받은 턴에서 먼저 검사해 통과하면 바로 끝낸다(<see cref="DiagnosisAgent"/>).
/// 여기 제출 도구 함수가 실제로 불리는 건 형식이 틀린 경우뿐이라, 같은 검사를 다시 돌려 오류를 모델에게 돌려준다.
/// </para>
/// </summary>
public sealed class DiagnosisTools
{
    public const string SubmitReport   = "submit_report";
    public const string SubmitFix      = "submit_fix";
    public const string DeclineRequest = "decline_request";

    /// <summary>묶음의 쓰임새. 대화 화면은 턴 종류(첫 진단 / 후속 질문)에 따라 둘로 나뉜다.</summary>
    private enum Mode { Diagnosis, Fix, ChatDiagnosis, ChatFollowUp }

    private static readonly object RepoProp   = new { type = "string", description = DiagnosisToolTexts.RepoArg };
    private static readonly object ServerProp = new { type = "string", @enum = new[] { "wms", "if" }, description = DiagnosisToolTexts.ServerArg };
    private static readonly object StartProp  = new { type = "integer", description = DiagnosisToolTexts.StartLineArg };
    private static readonly object EndProp    = new { type = "integer", description = DiagnosisToolTexts.EndLineArg };

    private readonly SourceMirror            _mirror;
    private readonly DiagnosisDbTools        _db;
    private readonly DiagnosisSeqTools       _seq;
    private readonly Mode                    _mode;
    private readonly string                  _env;
    private readonly int                     _maxToolCalls;
    private readonly Action<string, string?> _onStep;
    private int _calls;

    /// <param name="env">실행의 대상 환경(<see cref="DiagnosisEnvs"/>). DB 도구는 모두 이 환경으로 연결한다.</param>
    /// <param name="onStep">도구 호출마다 (화면용 한 줄 요약, 감사 로그용 상세 — 예: 실행한 SQL 전문)</param>
    public DiagnosisTools(
        SourceMirror mirror, DiagnosisDbTools db, DiagnosisSeqTools seq,
        string runKind, string env, int maxToolCalls, Action<string, string?> onStep)
        : this(mirror, db, seq, runKind == DiagnosisRunKinds.Fix ? Mode.Fix : Mode.Diagnosis, env, maxToolCalls, onStep)
    {
    }

    /// <summary>대화 화면 턴용 묶음. <paramref name="turnKind"/> 는 <see cref="DiagnosisTurnKinds"/>.</summary>
    public static DiagnosisTools ForChat(
        SourceMirror mirror, DiagnosisDbTools db, DiagnosisSeqTools seq,
        string turnKind, string env, int maxToolCalls, Action<string, string?> onStep)
        => new(mirror, db, seq, turnKind == DiagnosisTurnKinds.FollowUp ? Mode.ChatFollowUp : Mode.ChatDiagnosis,
               env, maxToolCalls, onStep);

    private DiagnosisTools(
        SourceMirror mirror, DiagnosisDbTools db, DiagnosisSeqTools seq,
        Mode mode, string env, int maxToolCalls, Action<string, string?> onStep)
    {
        _mirror        = mirror;
        _db            = db;
        _seq           = seq;
        _mode          = mode;
        _env           = env;
        _maxToolCalls  = maxToolCalls;
        _onStep        = onStep;
        SubmitToolName = mode == Mode.Fix ? SubmitFix : SubmitReport;
        IBetaRunnableTool[] lookups =
        [
            FindFilesTool(), SearchCodeTool(), ReadFileTool(),
            SearchDbObjectsTool(), ReadDbObjectTool(), RunQueryTool(),
            SearchLogsTool(),
        ];
        IBetaRunnableTool[] submits = mode switch
        {
            Mode.Fix       => [SubmitFixTool()],
            Mode.Diagnosis => [SubmitReportTool(), DeclineRequestTool()],
            _              => [SubmitReportTool(), SubmitFixTool(), DeclineRequestTool()],
        };
        Tools = [.. lookups, .. submits];
    }

    public string SubmitToolName { get; }
    public IReadOnlyList<IBetaRunnableTool> Tools { get; }
    public int ToolCalls => Volatile.Read(ref _calls);

    /// <summary>호출 상한·조사 뒤 거절 때 모델에게 줄 마무리 안내. 대화 후속 질문은 글로 답하게 한다.</summary>
    private string WrapUp => _mode == Mode.ChatFollowUp
        ? "지금까지 확인한 것으로 답하세요."
        : $"지금까지 확인한 근거로 {SubmitToolName} 을(를) 호출해 제출하세요.";

    /// <summary>상한 검사 → 진행 기록(+감사 상세) → 실행 → 결과 길이 제한 → 데이터 경계 표시.</summary>
    public async Task<string> GuardedAsync(string stepSummary, Func<Task<string>> body, string? auditDetail = null)
    {
        if (Interlocked.Increment(ref _calls) > _maxToolCalls)
            throw new DiagnosisToolException($"도구 호출 상한({_maxToolCalls}회)에 도달했습니다. {WrapUp}");
        _onStep(stepSummary, auditDetail);
        return DiagnosisTextFormat.MarkAsData(DiagnosisTextFormat.Truncate(await body()));
    }

    // ── 도구 정의 ─────────────────────────────────────────────

    private IBetaRunnableTool FindFilesTool() => Tool(
        "find_files",
        DiagnosisToolTexts.FindFiles,
        new()
        {
            ["name_pattern"] = new { type = "string", description = DiagnosisToolTexts.NamePatternArg },
            ["repo"]         = RepoProp,
        },
        ["name_pattern"],
        a => GuardedAsync($"파일 찾기: {ToolArgs.OptString(a, "name_pattern")}",
            () => Task.FromResult(_mirror.FindFiles(ToolArgs.Required(a, "name_pattern"), ToolArgs.OptString(a, "repo")))));

    private IBetaRunnableTool SearchCodeTool() => Tool(
        "search_code",
        DiagnosisToolTexts.SearchCode,
        new()
        {
            ["pattern"]     = new { type = "string", description = DiagnosisToolTexts.PatternArg },
            ["repo"]        = RepoProp,
            ["path_glob"]   = new { type = "string", description = DiagnosisToolTexts.PathGlobArg },
            ["ignore_case"] = new { type = "boolean", description = DiagnosisToolTexts.IgnoreCaseArg },
        },
        ["pattern"],
        a => GuardedAsync($"코드 검색: {ToolArgs.OptString(a, "pattern")}",
            () => Task.FromResult(_mirror.Search(
                ToolArgs.Required(a, "pattern"), ToolArgs.OptString(a, "repo"),
                ToolArgs.OptString(a, "path_glob"), ToolArgs.OptBool(a, "ignore_case", false)))));

    private IBetaRunnableTool ReadFileTool() => Tool(
        "read_file",
        DiagnosisToolTexts.ReadFile,
        new()
        {
            ["repo"]       = new { type = "string", description = DiagnosisToolTexts.ReadRepoArg },
            ["path"]       = new { type = "string", description = DiagnosisToolTexts.PathArg },
            ["start_line"] = StartProp,
            ["end_line"]   = EndProp,
        },
        ["repo", "path"],
        a => GuardedAsync($"파일 읽기: {ToolArgs.OptString(a, "repo")}/{ToolArgs.OptString(a, "path")} {ToolArgs.OptInt(a, "start_line") ?? 1}줄~",
            () => Task.FromResult(_mirror.ReadFile(
                ToolArgs.Required(a, "repo"), ToolArgs.Required(a, "path"),
                ToolArgs.OptInt(a, "start_line") ?? 1, ToolArgs.OptInt(a, "end_line")))));

    private IBetaRunnableTool SearchDbObjectsTool() => Tool(
        "search_db_objects",
        DiagnosisToolTexts.SearchDbObjects,
        new()
        {
            ["server"]   = ServerProp,
            ["database"] = new { type = "string", description = DiagnosisToolTexts.DatabaseAllArg },
            ["keyword"]  = new { type = "string", description = DiagnosisToolTexts.KeywordArg },
            ["types"]    = new { type = "array", items = new { type = "string", @enum = new[] { "procedure", "view", "function", "trigger" } } },
        },
        ["server", "keyword"],
        (a, ct) => GuardedAsync(
            $"DB 객체 검색: {ToolArgs.OptString(a, "server")}/{ToolArgs.OptString(a, "database") ?? "전체"} '{ToolArgs.OptString(a, "keyword")}'",
            () => _db.SearchObjectsAsync(
                _env, ToolArgs.Required(a, "server"), ToolArgs.OptString(a, "database"),
                ToolArgs.Required(a, "keyword"), ToolArgs.StringList(a, "types"), ct)));

    private IBetaRunnableTool ReadDbObjectTool() => Tool(
        "read_db_object",
        DiagnosisToolTexts.ReadDbObject,
        new()
        {
            ["server"]     = ServerProp,
            ["database"]   = new { type = "string", description = DiagnosisToolTexts.DatabaseArg },
            ["name"]       = new { type = "string", description = DiagnosisToolTexts.ObjectNameArg },
            ["start_line"] = StartProp,
            ["end_line"]   = EndProp,
        },
        ["server", "database", "name"],
        (a, ct) => GuardedAsync(
            $"DB 객체 읽기: {ToolArgs.OptString(a, "server")}/{ToolArgs.OptString(a, "database")}.{ToolArgs.OptString(a, "name")}",
            () => _db.ReadObjectAsync(
                _env, ToolArgs.Required(a, "server"), ToolArgs.Required(a, "database"), ToolArgs.Required(a, "name"),
                ToolArgs.OptInt(a, "start_line") ?? 1, ToolArgs.OptInt(a, "end_line"), ct)));

    private IBetaRunnableTool RunQueryTool() => Tool(
        "run_query",
        DiagnosisToolTexts.RunQuery,
        new()
        {
            ["server"]   = ServerProp,
            ["database"] = new { type = "string", description = DiagnosisToolTexts.QueryDatabaseArg },
            ["sql"]      = new { type = "string", description = DiagnosisToolTexts.SqlArg },
        },
        ["server", "database", "sql"],
        (a, ct) => GuardedAsync(
            $"조회 실행: {ToolArgs.OptString(a, "server")}/{ToolArgs.OptString(a, "database")} — {Preview(ToolArgs.OptString(a, "sql"))}",
            () => _db.RunQueryAsync(_env, ToolArgs.Required(a, "server"), ToolArgs.Required(a, "database"), ToolArgs.Required(a, "sql"), ct),
            auditDetail: ToolArgs.OptString(a, "sql")));

    private IBetaRunnableTool SearchLogsTool() => Tool(
        "search_logs",
        DiagnosisToolTexts.SearchLogs,
        new()
        {
            ["filter"] = new { type = "string", description = DiagnosisToolTexts.LogFilterArg },
            ["from"]   = new { type = "string", description = DiagnosisToolTexts.LogFromArg },
            ["to"]     = new { type = "string", description = DiagnosisToolTexts.LogToArg },
            ["count"]  = new { type = "integer", description = DiagnosisToolTexts.LogCountArg },
        },
        [],
        (a, ct) => GuardedAsync(
            $"로그 검색: {ToolArgs.OptString(a, "filter") ?? "전체"} ({ToolArgs.OptString(a, "from") ?? "24시간 전"} ~ {ToolArgs.OptString(a, "to") ?? "지금"})",
            () => _seq.SearchAsync(
                ToolArgs.OptString(a, "filter"), ToolArgs.OptString(a, "from"), ToolArgs.OptString(a, "to"),
                ToolArgs.OptInt(a, "count"), ct)));

    private IBetaRunnableTool SubmitReportTool() => Tool(
        SubmitReport,
        "진단 보고서를 제출하고 끝낸다. 한 번만 호출한다.",
        new()
        {
            ["verdict"] = new
            {
                type = "string", @enum = new[] { "issue", "expected" },
                description = "issue=코드·SP·설정 결함이 있거나 데이터 보정이 필요함, expected=지금 업무 규칙대로 동작해 고칠 것이 없음",
            },
            ["conclusion"] = new { type = "string", description = "한두 문장 결론. expected 면 운영에서 할 일(누구에게 무엇을 안내할지)도 적는다" },
            ["locations"] = new
            {
                type = "array",
                description = "발생 위치(가장 유력한 것부터). expected 면 그 동작을 정한 규칙의 위치(거절 분기 등)",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        kind   = new { type = "string", @enum = new[] { "code", "db" } },
                        target = new { type = "string", description = "code=저장소 이름, db=서버.DB (예: wms.WmsMain)" },
                        path   = new { type = "string", description = "code=파일 경로, db=스키마.객체" },
                        line   = new { type = "integer" },
                        note   = new { type = "string" },
                    },
                    required = new[] { "kind", "target", "path" },
                },
            },
            ["evidence"]   = new { type = "array", items = new { type = "string" }, description = "근거를 추적 순서대로(코드 경로 → SP 분기 → 조회 결과). 항목마다 파일:줄·객체:줄·조회 결과를 적는다" },
            ["confidence"] = new { type = "string", @enum = new[] { "high", "medium", "low" } },
            ["unverified"] = new { type = "array", items = new { type = "string" }, description = "확인하지 못한 것" },
        },
        ["verdict", "conclusion", "locations", "evidence", "confidence", "unverified"],
        a =>
        {
            DiagnosisSubmissionParser.ParseReport(a);
            return Task.FromResult("보고서를 받았습니다.");
        });

    private IBetaRunnableTool DeclineRequestTool() => Tool(
        DeclineRequest,
        "진단 범위 밖 요청(특정 이상을 가리키지 않는 요청)을 조사 없이 돌려보내고 끝낸다. 다른 도구를 쓰기 전에만 쓸 수 있다. "
        + "증상이 모호해도 특정 이상을 가리키면 거절하지 말고 진단한다.",
        new()
        {
            ["reason"] = new { type = "string", description = "진단할 수 없는 이유 한두 문장" },
            ["needed"] = new
            {
                type = "array", items = new { type = "string" },
                description = "진단하려면 보완할 입력을 이 요청에 맞춰(어느 화면·작업, 어떤 오류 문구, 언제, 어떤 번호). 보완해도 진단 대상이 아니면 빈 배열",
            },
        },
        ["reason", "needed"],
        a =>
        {
            if (ToolCalls > 0)
                throw new DiagnosisToolException(_mode == Mode.ChatFollowUp
                    ? "조사를 시작한 뒤에는 거절할 수 없습니다. 지금까지 확인한 것으로 답하세요."
                    : $"조사를 시작한 뒤에는 거절할 수 없습니다. 지금까지 확인한 근거로 {SubmitReport} 를 제출하세요.");
            DiagnosisSubmissionParser.ParseDecline(a);
            return Task.FromResult("거절을 받았습니다.");
        });

    private IBetaRunnableTool SubmitFixTool() => Tool(
        SubmitFix,
        "수정 방안을 제출하고 끝낸다. 한 번만 호출한다. 데이터 보정 SQL 은 텍스트로만 적는다(실행되지 않는다). "
        + "고칠 것이 없으면 fixes 를 빈 배열로 두고 no_fix_reason 에 이유를 적는다.",
        new()
        {
            ["fixes"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        kind    = new { type = "string", @enum = new[] { "code", "sp", "data", "config" } },
                        target  = new { type = "string", description = "저장소 이름 또는 서버.DB" },
                        path    = new { type = "string", description = "파일 경로 또는 스키마.객체(데이터 보정이면 생략 가능)" },
                        summary = new { type = "string", description = "무엇을 바꾸는지 한 줄" },
                        change  = new { type = "string", description = "바꿀 내용(코드·SQL). 보정 SQL 은 확인용 SELECT 와 함께" },
                        impact  = new { type = "string", description = "영향 범위" },
                        caution = new { type = "string", description = "주의점(배포 순서·운영 시간 등)" },
                    },
                    required = new[] { "kind", "target", "summary", "change" },
                },
            },
            ["no_fix_reason"] = new { type = "string", description = "고칠 것이 없다고 판단한 이유. 이때 fixes 는 빈 배열" },
            ["notes"]         = new { type = "string", description = "덧붙일 말" },
        },
        ["fixes"],
        a =>
        {
            if (_mode == Mode.ChatDiagnosis)
                throw new DiagnosisToolException("첫 진단에서는 수정 방안을 내지 않습니다 — submit_report 로 제출하세요.");
            DiagnosisSubmissionParser.ParseFix(a);
            return Task.FromResult("수정 방안을 받았습니다.");
        });

    // ── 헬퍼 ──────────────────────────────────────────────────

    private static IBetaRunnableTool Tool(
        string name, string description, Dictionary<string, object> properties, string[] required,
        Func<IReadOnlyDictionary<string, JsonElement>, Task<string>> run)
        => Tool(name, description, properties, required, (a, _) => run(a));

    private static IBetaRunnableTool Tool(
        string name, string description, Dictionary<string, object> properties, string[] required,
        Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<string>> run)
        => new BetaRunnableTool
        {
            Name = name,
            Definition = new BetaTool
            {
                Name        = name,
                Description = description,
                InputSchema = new InputSchema
                {
                    Properties = properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                    Required   = required,
                },
            },
            Run = async (use, ct) => (BetaToolResultBlockParamContent)await run(use.Input, ct),
        };

    private static string Preview(string? sql)
    {
        var oneLine = string.Join(' ', (sql ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 80 ? oneLine : oneLine[..80] + "…";
    }
}

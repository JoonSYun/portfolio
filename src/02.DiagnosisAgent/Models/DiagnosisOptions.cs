namespace Portfolio.DiagnosisAgent.Models;

/// <summary>
/// appsettings.json 의 <c>Diagnosis</c> 섹션. 배포 환경(Development/Staging/Production)과 상관없이 한 곳에서 관리한다.
/// 진단 DB 연결은 같은 파일 ConnectionStrings 의 <c>Diag_WMS_TEST</c>·<c>Diag_WMS_PROD</c>(server=wms) /
/// <c>Diag_IF_TEST</c>·<c>Diag_IF_PROD</c>(server=if) — 실행마다 고른 대상 환경이 둘 중 하나를 정한다.
/// </summary>
public sealed class DiagnosisOptions
{
    public string AnthropicApiKey     { get; set; } = "";
    public string Model               { get; set; } = "claude-opus-5-5";
    public string Effort              { get; set; } = "high";
    public int    MaxToolCalls        { get; set; } = 40;
    public int    TimeoutMinutes      { get; set; } = 10;
    public int    MaxConcurrentRuns   { get; set; } = 2;
    public int    RunRetentionHours   { get; set; } = 24;
    public string SourceRoot          { get; set; } = "";
    public int    SyncIntervalMinutes { get; set; } = 30;
    /// <summary>대화 화면: 원문(모델에 다시 보낼 API 메시지)을 마지막 활동 후 며칠 두는지. 지나면 원문만 지운다.</summary>
    public int    TranscriptRetentionDays   { get; set; } = 30;
    /// <summary>대화 화면: 입력 토큰이 이 값을 넘으면 API 가 오래된 도구 결과를 지운다(clear_tool_uses trigger).</summary>
    public long   ContextClearTriggerTokens { get; set; } = 200_000;
    /// <summary>대화 화면: 도구 결과를 지울 때 남기는 최근 도구 호출 수(keep).</summary>
    public long   ContextClearKeepToolUses  { get; set; } = 10;
    /// <summary>대화 화면: 한 번에 이만큼 이상 지울 수 있을 때만 지운다(clear_at_least) — 지울 때마다 캐시가 다시 시작된다.</summary>
    public long   ContextClearAtLeastTokens { get; set; } = 20_000;
    /// <summary>MCP 공개(docs/DIAGNOSIS_MCP_DESIGN_2026-10.md).</summary>
    public DiagnosisMcpOptions Mcp { get; set; } = new();
    public DiagnosisGitHubOptions GitHub { get; set; } = new();
    /// <summary>로그 조회 도구 search_logs 가 읽는 Seq.</summary>
    public DiagnosisSeqOptions Seq { get; set; } = new();
}

public sealed class DiagnosisSeqOptions
{
    /// <summary>
    /// 이 관리자 서버의 Seq. localhost 라 운영 Admin 은 운영 서버 Seq, 테스트 서버 Admin 은 테스트 서버 Seq 를 본다
    /// — 실행의 대상 환경(env)과 상관없고, 결과 첫 줄에 출처를 적는다.
    /// </summary>
    public string Url { get; set; } = "http://localhost:5341";

    /// <summary>
    /// Read 권한 API 키. 두 Seq 에 같은 토큰으로 만들어(seqcli apikey create --token) 이 한 곳에 둔다.
    /// 비우면 키 없이 부른다(인증을 끈 개발 PC Seq).
    /// </summary>
    public string ApiKey { get; set; } = "";
}

public sealed class DiagnosisGitHubOptions
{
    public string Owner { get; set; } = "acme-logistics";

    /// <summary>소유자(Owner) 저장소 Contents 읽기 전용 fine-grained 토큰. 저장소 접근 범위가 곧 동기화 범위다.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// 비워 두면 토큰이 볼 수 있는 소유자(Owner) 저장소 전체(보관·빈 저장소 제외)를 각 기본 브랜치로 동기화한다.
    /// 적으면 적은 저장소만 동기화한다.
    /// </summary>
    public List<DiagnosisRepoOptions> Repos { get; set; } = new();
}

public sealed class DiagnosisRepoOptions
{
    public string Name   { get; set; } = "";
    public string Branch { get; set; } = "main";
}

public sealed class DiagnosisMcpOptions
{
    /// <summary>
    /// <c>/mcp</c> 에 들어올 수 있는 요청 IP — 주소 하나(<c>203.0.113.10</c>) 또는 대역(<c>10.0.0.0/24</c>). 비우면 모두 막는다.
    /// 코드 기본값을 두지 않는다 — 설정 바인딩은 목록에 덧붙이므로 기본값이 있으면 설정에서 뺄 수 없다.
    /// 환경별 파일에서 바꿀 때는 배열이 인덱스별로 합쳐지므로 전체 목록을 다시 적는다.
    /// </summary>
    public List<string> AllowedIps { get; set; } = new();

    /// <summary>
    /// HTTPS 요청만 받는다(기본). 운영은 인터넷에 열려 있어 켜 두고, HTTP 만 열린 사내 테스트 서버는
    /// appsettings.Staging.json 에서 끈다.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>개인 토큰 유효 일수(발급 시각부터).</summary>
    public int TokenLifetimeDays { get; set; } = 365;
}

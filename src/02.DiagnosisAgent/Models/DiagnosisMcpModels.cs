namespace Portfolio.DiagnosisAgent.Models;

/// <summary>
/// 진단 MCP 개인 토큰(1인 1개, PK=UserId). 원문은 저장하지 않고 SHA-256 해시(대문자 hex 64자)만 둔다. 시각은 KST.
/// 테이블은 raw SQL(Infrastructure/Auth/Sql/2026-10-15_Add_DiagnosisMcpToken.sql)로 관리한다.
/// </summary>
public sealed class DiagnosisMcpTokenEntity
{
    public string    UserId      { get; set; } = "";
    public string    TokenHash   { get; set; } = "";
    /// <summary>목록에서 알아보는 앞부분(예: <c>wsd_Ab3k9Q</c>). 화면이 뒤에 "…" 를 붙인다.</summary>
    public string    TokenPrefix { get; set; } = "";
    public DateTime  CreatedAt   { get; set; }
    public DateTime  ExpiresAt   { get; set; }
    public DateTime? LastUsedAt  { get; set; }
    public string?   LastUsedIp  { get; set; }
}

/// <summary>
/// MCP 요청 — 사용자에게 알리는 요청 ID 1건. 한 주제의 조사에서 부른 호출(<see cref="DiagnosisMcpCallEntity"/>)을 묶는다.
/// 질문·답변 내용은 두지 않는다. 시각은 KST. 테이블은 raw SQL(Infrastructure/Auth/Sql/2026-11-11_Add_DiagnosisMcpRequest.sql)로 관리한다.
/// </summary>
public sealed class DiagnosisMcpRequestEntity
{
    /// <summary>요청 ID — "req_" + 무작위 소문자·숫자 8자. 사용자에게 고지하는 값.</summary>
    public string   Id        { get; set; } = "";
    /// <summary>AspNetUsers.Id(개인 토큰 소유자). 외래 키 없음 — 계정을 지워도 기록 유지.</summary>
    public string   UserId    { get; set; } = "";
    /// <summary>그 시점의 이메일.</summary>
    public string   UserEmail { get; set; } = "";
    public string   ClientIp  { get; set; } = "";
    /// <summary>클라이언트 이름·버전(User-Agent 헤더 그대로).</summary>
    public string?  UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// MCP 호출 — 도구·프롬프트 호출 1번 = 1행. 앱은 넣기만 한다(수정·삭제 없음).
/// 조회 결과 본문은 두지 않고 크기만 남긴다. 시각은 KST. 테이블은 raw SQL(Infrastructure/Auth/Sql/2026-11-11_Add_DiagnosisMcpRequest.sql)로 관리한다.
/// </summary>
public sealed class DiagnosisMcpCallEntity
{
    public long     Id                { get; set; }
    /// <summary>보낸 요청 ID 그대로(없는 번호·남의 번호도). read_guide·start_request·프롬프트는 null. 외래 키 없음.</summary>
    public string?  RequestId         { get; set; }
    public string   UserId            { get; set; } = "";
    public string   UserEmail         { get; set; } = "";
    public string   ClientIp          { get; set; } = "";
    /// <summary>MCP 메서드(<see cref="DiagnosisMcpMethods"/>).</summary>
    public string   Method            { get; set; } = "";
    /// <summary>도구·프롬프트 이름.</summary>
    public string   Name              { get; set; } = "";
    /// <summary>DB 도구·진단 프롬프트의 대상 — prod · test / wms · if / DB 이름. 그 밖 호출은 null.</summary>
    public string?  TargetEnvironment { get; set; }
    public string?  TargetServer      { get; set; }
    public string?  TargetDatabase    { get; set; }
    /// <summary>받은 인자 전체 JSON(SQL 전문·검색어·경로·기간). 비밀값 가림 적용.</summary>
    public string   Arguments         { get; set; } = "";
    /// <summary>결과 구분(<see cref="DiagnosisMcpOutcomes"/>).</summary>
    public string   Outcome           { get; set; } = "";
    /// <summary>거절 사유·오류 문구(가림 적용).</summary>
    public string?  ErrorMessage      { get; set; }
    /// <summary>돌려준 결과 크기(UTF-8 바이트). 내용은 두지 않는다.</summary>
    public int?     ResponseSize      { get; set; }
    public DateTime StartedAt         { get; set; }
    public int      DurationMs        { get; set; }
    /// <summary>클라이언트 호출 번호(Claude Code 의 tool_use id). 사용자 PC 대화 기록과 연결.</summary>
    public string?  ToolCallId        { get; set; }
    /// <summary>서버 요청 추적 번호(W3C trace id, 없으면 ASP.NET 요청 번호). Seq 로그와 연결.</summary>
    public string?  TraceId           { get; set; }
}

public static class DiagnosisMcpMethods
{
    public const string ToolsCall  = "tools/call";
    public const string PromptsGet = "prompts/get";
}

/// <summary>MCP 호출 결과 구분. DB CHECK 제약과 같은 값.</summary>
public static class DiagnosisMcpOutcomes
{
    public const string Success   = "SUCCESS";
    /// <summary>서버 검사 거절 — SQL 검사·요청 ID 없음·인자 오류·없는 도구.</summary>
    public const string Rejected  = "REJECTED";
    /// <summary>실행 오류 — DB 연결·SQL 실행·Seq 오류·시간 초과·서버 설정 누락.</summary>
    public const string Failed    = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public static class DiagnosisMcpTokenStates
{
    public const string None    = "none";
    public const string Active  = "active";
    public const string Expired = "expired";
}

/// <summary>내 토큰 상태. 토큰이 없으면 State=none 이고 나머지는 null.</summary>
public sealed record DiagnosisMcpTokenStatusDto(
    string State, string? Prefix, DateTime? CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt, string? LastUsedIp);

/// <summary>발급 직후 응답 — 토큰 원문은 이때 한 번만 내려간다.</summary>
public sealed record DiagnosisMcpTokenIssuedDto(string Token, DiagnosisMcpTokenStatusDto Status);

/// <summary>MCP 토큰 관리 화면 한 줄. UserName 은 로그인 ID.</summary>
public sealed record DiagnosisMcpTokenRowDto(
    string UserId, string UserName, string Name, string Prefix,
    DateTime CreatedAt, DateTime ExpiresAt, bool Expired, DateTime? LastUsedAt, string? LastUsedIp);

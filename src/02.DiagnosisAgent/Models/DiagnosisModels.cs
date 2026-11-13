namespace Portfolio.DiagnosisAgent.Models;

/// <summary>실행 종류. 진단과 수정 방안은 같은 도구를 쓰고 첫 메시지·제출 도구만 다르다.</summary>
public static class DiagnosisRunKinds
{
    public const string Diagnosis = "diagnosis";
    public const string Fix       = "fix";
}

public static class DiagnosisRunStatuses
{
    public const string Queued    = "queued";
    public const string Running   = "running";
    public const string Succeeded = "succeeded";
    /// <summary>진단 범위 밖(특정 이상을 가리키지 않는 요청)이라 조사 없이 돌려보냈다.</summary>
    public const string Rejected  = "rejected";
    public const string Failed    = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>진단 대상 환경. 실행마다 사람이 고르고, DB 도구는 그 환경의 진단 연결만 쓴다(모델은 server 만 고른다).</summary>
public static class DiagnosisEnvs
{
    public const string Test = "test";
    public const string Prod = "prod";
}

/// <summary>화면의 진단 요청. 대상 환경과 증상(오류 원문)이 필수.</summary>
public sealed record DiagnosisRequest
{
    /// <summary><see cref="DiagnosisEnvs"/> 중 하나. 수정 방안 실행은 원 진단의 요청을 그대로 받아 같은 환경을 쓴다.</summary>
    public string? Env         { get; init; }
    /// <summary>WMS 화면코드 또는 스케줄러 작업명(DOMAIN_CODE) 등 오류가 난 곳의 이름.</summary>
    public string? ScreenCode  { get; init; }
    /// <summary>오류가 날 때 한 동작(누른 버튼·메뉴 등). 버튼 → 처리기 → SP 로 바로 따라가게 해 준다.</summary>
    public string? UserAction  { get; init; }
    public string? Identifiers { get; init; }
    public string? Symptom     { get; init; }
}

/// <summary>submit_report 로 받은 진단 보고서.</summary>
public sealed record DiagnosisReport
{
    /// <summary>issue=결함·데이터 보정 필요, expected=업무 규칙대로 동작해 고칠 것이 없음(위치는 그 규칙의 자리).</summary>
    public required string                           Verdict    { get; init; }   // issue | expected
    public required string                           Conclusion { get; init; }
    public required string                           Confidence { get; init; }   // high | medium | low
    public required IReadOnlyList<DiagnosisLocation> Locations  { get; init; }
    public required IReadOnlyList<string>            Evidence   { get; init; }
    public required IReadOnlyList<string>            Unverified { get; init; }
}

public sealed record DiagnosisLocation
{
    public required string Kind   { get; init; }   // code | db
    public required string Target { get; init; }   // code=저장소, db=서버.DB
    public required string Path   { get; init; }   // code=파일 경로, db=스키마.객체
    public int?            Line   { get; init; }
    public string?         Note   { get; init; }
}

/// <summary>decline_request 로 받은 거절. 조사를 시작하기 전에만 받는다.</summary>
public sealed record DiagnosisDecline
{
    public required string                Reason { get; init; }
    /// <summary>진단하려면 보완할 입력. 보완해도 진단 대상이 아니면 비어 있다.</summary>
    public required IReadOnlyList<string> Needed { get; init; }
}

/// <summary>
/// submit_fix 로 받은 수정 방안. 보정 SQL 은 텍스트일 뿐 실행되지 않는다.
/// 고칠 것이 없으면 Fixes 는 비고 NoFixReason 에 이유가 있다(둘 중 하나만).
/// </summary>
public sealed record DiagnosisFixProposal
{
    public required IReadOnlyList<DiagnosisFix> Fixes       { get; init; }
    public string?                              NoFixReason { get; init; }
    public string?                              Notes       { get; init; }
}

public sealed record DiagnosisFix
{
    public required string Kind    { get; init; }   // code | sp | data | config
    public required string Target  { get; init; }
    public string?         Path    { get; init; }
    public required string Summary { get; init; }
    public required string Change  { get; init; }
    public string?         Impact  { get; init; }
    public string?         Caution { get; init; }
}

public sealed record DiagnosisStepDto
{
    public required DateTime At      { get; init; }
    public required string   Summary { get; init; }
}

public sealed record DiagnosisRunDto
{
    public required string                              Id               { get; init; }
    public required string                              Kind             { get; init; }
    public required string                              Status           { get; init; }
    public required DiagnosisRequest                    Request          { get; init; }
    public required string                              StartedBy        { get; init; }
    public required DateTime                            CreatedAt        { get; init; }
    public DateTime?                                    StartedAt        { get; init; }
    public DateTime?                                    FinishedAt       { get; init; }
    public required IReadOnlyList<DiagnosisStepDto>     Steps            { get; init; }
    public DiagnosisReport?                             Report           { get; init; }
    public DiagnosisFixProposal?                        Fix              { get; init; }
    public DiagnosisDecline?                            Decline          { get; init; }
    public string?                                      FinalText        { get; init; }
    public string?                                      Error            { get; init; }
    public long                                         InputTokens      { get; init; }
    public long                                         OutputTokens     { get; init; }
    public long                                         CacheReadTokens  { get; init; }
    public long                                         CacheWriteTokens { get; init; }
    public int                                          ToolCalls        { get; init; }
    public required IReadOnlyDictionary<string, string> SourceShas       { get; init; }
    public string?                                      ParentRunId      { get; init; }
    public string?                                      FixRunId         { get; init; }
}

/// <summary>소스 복사본의 저장소별 상태. 화면의 "소스 기준" 표시와 진단 기록에 쓴다.</summary>
public sealed record SourceRepoStateDto
{
    public required string Name      { get; init; }
    public string?         Sha       { get; init; }
    public DateTime?       SyncedAt  { get; init; }
    public DateTime?       CheckedAt { get; init; }
    public string?         Error     { get; init; }
}

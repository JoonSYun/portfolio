using System.Text.Encodings.Web;
using System.Text.Json;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>실행의 첫 사용자 메시지. 진단은 입력만, 수정 방안은 입력 + 원 진단 보고서를 넘긴다.</summary>
public static class DiagnosisPrompts
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented        = true,
        Encoder              = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <param name="repos">지금 소스 복사본에 있는 저장소 이름. 저장소 전체를 동기화하므로 매번 목록을 알려 준다.</param>
    public static string FirstMessage(DiagnosisRun run, IReadOnlyCollection<string> repos)
        => run.Kind == DiagnosisRunKinds.Fix
            ? FixMessage(run.Request, run.ParentReport!, repos)
            : DiagnosisMessage(run.Request, repos);

    private static string Inputs(DiagnosisRequest r) => $"""
        대상 환경: {(r.Env == DiagnosisEnvs.Prod ? "운영" : "테스트")}
        화면코드 / 작업명: {Or(r.ScreenCode)}
        동작(누른 버튼 등): {Or(r.UserAction)}
        식별자:
        {Or(r.Identifiers)}
        오류 원문 / 증상:
        {Or(r.Symptom)}
        """;

    private static string Repos(IReadOnlyCollection<string> repos) => $"""
        [소스 복사본 저장소]
        {(repos.Count == 0 ? "(없음 — 동기화 전)" : string.Join(", ", repos))}
        """;

    private static string DiagnosisMessage(DiagnosisRequest r, IReadOnlyCollection<string> repos) => $"""
        [진단 요청]
        {Inputs(r)}

        {Repos(repos)}

        진단 범위 밖 요청이면 다른 도구를 쓰기 전에 decline_request 로 돌려보내고,
        아니면 원인 위치를 찾아 submit_report 로 제출하세요.
        """;

    private static string FixMessage(DiagnosisRequest r, DiagnosisReport report, IReadOnlyCollection<string> repos) => $"""
        [수정 방안 요청]
        아래 진단 요청과 보고서를 바탕으로 수정 방안을 작성해 submit_fix 로 제출하세요.
        보고서의 발생 위치를 먼저 다시 읽어 확인한 뒤 작성하세요.
        다시 확인해도 고칠 것이 없으면 fixes 를 비우고 no_fix_reason 에 이유를 적어 제출하세요.

        {Inputs(r)}

        {Repos(repos)}

        [진단 보고서]
        {JsonSerializer.Serialize(report, ReportJson)}
        """;

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "(없음)" : value.Trim();
}

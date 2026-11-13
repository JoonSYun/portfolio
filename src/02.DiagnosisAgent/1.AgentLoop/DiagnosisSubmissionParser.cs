using System.Text.Json;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// submit_report / submit_fix 입력을 검사해 모델로 바꾼다. 틀린 곳을 모두 모아 한 번에 알려 줘야
/// 모델이 한 번에 고쳐 다시 낸다.
/// </summary>
public static class DiagnosisSubmissionParser
{
    public static readonly string[]  Verdicts      = ["issue", "expected"];
    private static readonly string[] Confidences   = ["high", "medium", "low"];
    private static readonly string[] LocationKinds = ["code", "db"];
    private static readonly string[] FixKinds      = ["code", "sp", "data", "config"];

    public static DiagnosisReport ParseReport(IReadOnlyDictionary<string, JsonElement> input)
    {
        var errors = new List<string>();

        var verdict = ToolArgs.OptString(input, "verdict");
        if (verdict is null || !Verdicts.Contains(verdict))
            errors.Add("verdict 는 issue/expected 중 하나여야 합니다.");

        var conclusion = ToolArgs.OptString(input, "conclusion");
        if (string.IsNullOrWhiteSpace(conclusion)) errors.Add("conclusion 이 비어 있습니다.");

        var confidence = ToolArgs.OptString(input, "confidence");
        if (confidence is null || !Confidences.Contains(confidence))
            errors.Add("confidence 는 high/medium/low 중 하나여야 합니다.");

        var locations = new List<DiagnosisLocation>();
        foreach (var el in ToolArgs.Array(input, "locations"))
        {
            var kind   = ToolArgs.Prop(el, "kind");
            var target = ToolArgs.Prop(el, "target");
            var path   = ToolArgs.Prop(el, "path");
            if (kind is null || !LocationKinds.Contains(kind) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(path))
            {
                errors.Add("locations 의 각 항목에는 kind(code/db)·target·path 가 있어야 합니다.");
                locations.Clear();
                break;
            }
            locations.Add(new DiagnosisLocation
            {
                Kind = kind, Target = target!, Path = path!,
                Line = ToolArgs.PropInt(el, "line"), Note = ToolArgs.Prop(el, "note"),
            });
        }
        if (locations.Count == 0 && !errors.Any(e => e.StartsWith("locations")))
            errors.Add("locations 가 비어 있습니다. 확정하지 못했다면 가장 유력한 후보를 넣고 confidence 를 low 로 하세요.");

        var evidence = ToolArgs.StringList(input, "evidence");
        if (evidence.Count == 0) errors.Add("evidence 가 비어 있습니다. 근거를 추적 순서대로 적으세요.");

        if (errors.Count > 0) throw new DiagnosisToolException("보고서 형식 오류: " + string.Join(" ", errors));

        return new DiagnosisReport
        {
            Verdict    = verdict!,
            Conclusion = conclusion!,
            Confidence = confidence!,
            Locations  = locations,
            Evidence   = evidence,
            Unverified = ToolArgs.StringList(input, "unverified"),
        };
    }

    public static DiagnosisDecline ParseDecline(IReadOnlyDictionary<string, JsonElement> input)
    {
        var reason = ToolArgs.OptString(input, "reason");
        if (string.IsNullOrWhiteSpace(reason)) throw new DiagnosisToolException("거절 형식 오류: reason 이 비어 있습니다.");
        return new DiagnosisDecline { Reason = reason, Needed = ToolArgs.StringList(input, "needed") };
    }

    public static DiagnosisFixProposal ParseFix(IReadOnlyDictionary<string, JsonElement> input)
    {
        var errors = new List<string>();
        var fixes  = new List<DiagnosisFix>();
        foreach (var el in ToolArgs.Array(input, "fixes"))
        {
            var kind    = ToolArgs.Prop(el, "kind");
            var target  = ToolArgs.Prop(el, "target");
            var summary = ToolArgs.Prop(el, "summary");
            var change  = ToolArgs.Prop(el, "change");
            if (kind is null || !FixKinds.Contains(kind) || string.IsNullOrWhiteSpace(target)
                || string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(change))
            {
                errors.Add("fixes 의 각 항목에는 kind(code/sp/data/config)·target·summary·change 가 있어야 합니다.");
                break;
            }
            fixes.Add(new DiagnosisFix
            {
                Kind = kind, Target = target!, Path = ToolArgs.Prop(el, "path"),
                Summary = summary!, Change = change!,
                Impact = ToolArgs.Prop(el, "impact"), Caution = ToolArgs.Prop(el, "caution"),
            });
        }
        var noFixReason = ToolArgs.OptString(input, "no_fix_reason");
        var hasReason   = !string.IsNullOrWhiteSpace(noFixReason);
        if (fixes.Count == 0 && errors.Count == 0 && !hasReason)
            errors.Add("fixes 가 비어 있습니다. 고칠 것이 없으면 no_fix_reason 에 이유를 적으세요.");
        if (fixes.Count > 0 && hasReason)
            errors.Add("fixes 와 no_fix_reason 은 함께 쓸 수 없습니다. 고칠 것이 있으면 no_fix_reason 을 비우세요.");
        if (errors.Count > 0) throw new DiagnosisToolException("수정 방안 형식 오류: " + string.Join(" ", errors));

        return new DiagnosisFixProposal
        {
            Fixes       = fixes,
            NoFixReason = hasReason ? noFixReason : null,
            Notes       = ToolArgs.OptString(input, "notes"),
        };
    }
}

using System.Text.Json;
using Anthropic;
using Anthropic.Helpers.Beta;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 실행 1건(진단 또는 수정 방안)을 <c>BetaToolRunner</c> 로 돌린다.
/// <para>
/// 헬퍼가 도구 실행·결과 반환·thinking 블록 되돌려 보내기·거절 시 대체 모델 처리를 맡고, 여기서는 턴마다
/// 사용량을 쌓고 제출 도구(진단 실행은 decline_request 포함) 호출을 찾아 형식이 맞으면 바로 끝낸다(제출 뒤 불필요한 API 호출을 하지 않게).
/// 형식이 틀리면 반복을 이어 가 제출 도구 함수가 오류를 돌려주게 한다(<see cref="DiagnosisTools"/>).
/// </para>
/// </summary>
public sealed class DiagnosisAgent
{
    private const int MaxTokens = 64_000;

    private readonly DiagnosisOptions        _options;
    private readonly SourceMirror            _mirror;
    private readonly DiagnosisDbTools        _db;
    private readonly DiagnosisSeqTools       _seq;
    private readonly KnowledgeBase           _knowledge;
    private readonly ILogger<DiagnosisAgent> _logger;
    private readonly AnthropicClient         _client;

    public DiagnosisAgent(
        IOptions<DiagnosisOptions> options,
        SourceMirror               mirror,
        DiagnosisDbTools           db,
        DiagnosisSeqTools          seq,
        KnowledgeBase              knowledge,
        ILogger<DiagnosisAgent>    logger)
    {
        _options   = options.Value;
        _mirror    = mirror;
        _db        = db;
        _seq       = seq;
        _knowledge = knowledge;
        _logger    = logger;
        _client    = new AnthropicClient { ApiKey = _options.AnthropicApiKey };
    }

    public async Task RunAsync(DiagnosisRun run, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AnthropicApiKey))
        {
            run.Fail("Diagnosis:AnthropicApiKey 가 설정되지 않았습니다.");
            return;
        }

        // 진행 기록은 화면용(메모리), 감사 로그는 Serilog — 운영 DB 에 실행한 SQL 은 전문을 남긴다.
        var tools = new DiagnosisTools(_mirror, _db, _seq, run.Kind, run.Request.Env!, _options.MaxToolCalls, (summary, detail) =>
        {
            run.AddStep(summary);
            _logger.LogInformation(AppLog.Log("[Diagnosis] 도구 id={Id} {Summary} {Detail}"), run.Id, summary, detail);
        });
        var parameters = new MessageCreateParams
        {
            Model        = _options.Model,
            MaxTokens    = MaxTokens,
            System       = _knowledge.SystemPrompt,
            Messages     = [new() { Role = Role.User, Content = DiagnosisPrompts.FirstMessage(run, AvailableRepos()) }],
            Thinking     = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = _options.Effort },
            CacheControl = new BetaCacheControlEphemeral(),
            // 모델이 드물게 거절하면 서버가 대체 모델로 다시 시도한다.
            Fallbacks    = new BetaFallbacksParam(new Default()),
            Betas        = [AnthropicBeta.ServerSideFallback2026_07_01],
        };

        var runner = _client.Beta.Messages.ToolRunner(parameters, tools.Tools, maxIterations: _options.MaxToolCalls + 5);

        BetaMessage? last = null;
        await foreach (var message in runner.WithCancellation(ct))
        {
            last = message;
            var u = message.Usage;
            run.AddUsage(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0);
            run.SetToolCalls(tools.ToolCalls);

            foreach (var block in message.Content)
            {
                if (!block.TryPickToolUse(out var use)) continue;
                if (use.Name == tools.SubmitToolName && TryAccept(run, use.Input)) return;
                // 거절은 조회 도구를 쓰기 전에만 받는다. 조사 뒤의 거절은 도구 함수가 오류로 돌려 보고서를 내게 한다.
                if (use.Name == DiagnosisTools.DeclineRequest && tools.ToolCalls == 0 && TryDecline(run, use.Input)) return;
            }
        }

        var refused = last?.StopReason?.Value() == BetaStopReason.Refusal;
        run.Fail(refused ? "모델이 요청을 거절했습니다." : "보고서를 제출하지 않고 끝났습니다.", ExtractText(last));
    }

    private IReadOnlyCollection<string> AvailableRepos()
        => _mirror.GetStates().Where(s => s.Sha is not null).Select(s => s.Name).ToList();

    private static bool TryAccept(DiagnosisRun run, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            if (run.Kind == DiagnosisRunKinds.Fix) run.SucceedWithFix(DiagnosisSubmissionParser.ParseFix(input));
            else run.SucceedWithReport(DiagnosisSubmissionParser.ParseReport(input));
            return true;
        }
        catch (DiagnosisToolException)
        {
            return false;
        }
    }

    private static bool TryDecline(DiagnosisRun run, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            run.Reject(DiagnosisSubmissionParser.ParseDecline(input));
            return true;
        }
        catch (DiagnosisToolException)
        {
            return false;
        }
    }

    private static string? ExtractText(BetaMessage? message)
    {
        if (message is null) return null;
        var texts = message.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join("\n", texts);
        return joined.Length == 0 ? null : joined;
    }
}

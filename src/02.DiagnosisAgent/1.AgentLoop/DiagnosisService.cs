using System.Diagnostics;
using Microsoft.Extensions.Options;
using Portfolio.Admin.Models;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 진단·수정 방안 실행을 만들고 백그라운드로 돌린다. 동시 실행은 <see cref="DiagnosisRunStore.Slots"/> 만큼,
/// 실행마다 시간 상한을 건다. 시작·종료를 감사 로그로 남긴다.
/// </summary>
public sealed class DiagnosisService
{
    private const int MaxSymptomChars     = 4000;
    private const int MaxIdentifiersChars = 4000;
    private const int MaxScreenCodeChars  = 100;
    private const int MaxUserActionChars  = 500;

    private readonly DiagnosisRunStore         _store;
    private readonly DiagnosisAgent            _agent;
    private readonly SourceMirror              _mirror;
    private readonly DiagnosisOptions          _options;
    private readonly IHostApplicationLifetime  _lifetime;
    private readonly ILogger<DiagnosisService> _logger;

    public DiagnosisService(
        DiagnosisRunStore          store,
        DiagnosisAgent             agent,
        SourceMirror               mirror,
        IOptions<DiagnosisOptions> options,
        IHostApplicationLifetime   lifetime,
        ILogger<DiagnosisService>  logger)
    {
        _store    = store;
        _agent    = agent;
        _mirror   = mirror;
        _options  = options.Value;
        _lifetime = lifetime;
        _logger   = logger;
    }

    public DiagnosisRun StartDiagnosis(DiagnosisRequest request, string user)
    {
        if (request.Env is not (DiagnosisEnvs.Test or DiagnosisEnvs.Prod))
            throw new BusinessRuleException("대상 환경(운영·테스트)을 고르세요.");
        var symptom = request.Symptom?.Trim();
        if (string.IsNullOrEmpty(symptom))
            throw new BusinessRuleException("오류 원문이나 증상을 입력하세요.");
        if (symptom.Length > MaxSymptomChars
            || (request.Identifiers?.Length ?? 0) > MaxIdentifiersChars
            || (request.ScreenCode?.Length ?? 0) > MaxScreenCodeChars
            || (request.UserAction?.Length ?? 0) > MaxUserActionChars)
            throw new BusinessRuleException("입력이 너무 깁니다(증상·식별자 4,000자, 동작 500자, 화면코드 100자 이내).");

        var run = _store.Add(new DiagnosisRun(DiagnosisRunKinds.Diagnosis, request with { Symptom = symptom }, user));
        Launch(run);
        return run;
    }

    public DiagnosisRun StartFix(string diagnosisRunId, string user)
    {
        var source = _store.Get(diagnosisRunId);
        if (source.Kind != DiagnosisRunKinds.Diagnosis || source.Report is null)
            throw new BusinessRuleException("성공한 진단에만 수정 방안을 요청할 수 있습니다.", StatusCodes.Status409Conflict);
        // 수정 방안은 진단 1건당 하나씩만 진행한다. 끝난 뒤 다시 요청하면 새 실행이 연결을 대신한다.
        if (source.FixRunId is { } previous && _store.TryGet(previous, out var prevRun) && prevRun is { IsFinished: false })
            throw new BusinessRuleException("이미 수정 방안을 작성하고 있습니다.", StatusCodes.Status409Conflict);

        var run = _store.Add(new DiagnosisRun(DiagnosisRunKinds.Fix, source.Request, user, source.Id, source.Report));
        source.LinkFix(run.Id);
        Launch(run);
        return run;
    }

    public DiagnosisRun Cancel(string id)
    {
        var run = _store.Get(id);
        run.Cancellation.Cancel();
        return run;
    }

    private void Launch(DiagnosisRun run) => _ = Task.Run(() => ExecuteAsync(run));

    private async Task ExecuteAsync(DiagnosisRun run)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation(AppLog.Log(
            "[Diagnosis] 시작 id={Id} kind={Kind} env={Env} user={User} screen={Screen} action={Action} ids={Ids} symptom={Symptom} parent={Parent}"),
            run.Id, run.Kind, run.Request.Env, run.StartedBy, run.Request.ScreenCode, run.Request.UserAction, run.Request.Identifiers,
            run.Request.Symptom, run.ParentRunId);

        // 시간 상한은 실행 시간에만 건다 — 슬롯을 기다리는 동안은 취소·서버 종료만 받는다.
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token, _lifetime.ApplicationStopping);
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked  = null;
        var acquired = false;
        try
        {
            await _store.Slots.WaitAsync(waiting.Token);
            acquired = true;

            timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes)));
            linked  = CancellationTokenSource.CreateLinkedTokenSource(
                run.Cancellation.Token, timeout.Token, _lifetime.ApplicationStopping);
            run.MarkRunning(_mirror.CurrentShas());
            await _agent.RunAsync(run, linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (run.Cancellation.IsCancellationRequested) run.MarkCancelled();
            else if (timeout?.IsCancellationRequested == true) run.Fail($"시간 상한({_options.TimeoutMinutes}분)을 넘었습니다.");
            else run.Fail("서버가 종료되어 중단되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, AppLog.Log("[Diagnosis] 실패 id={Id}"), run.Id);
            run.Fail($"실행 중 오류: {ex.Message}");
        }
        finally
        {
            linked?.Dispose();
            timeout?.Dispose();
            if (acquired) _store.Slots.Release();
            var dto = run.ToDto();
            _logger.LogInformation(AppLog.Log(
                "[Diagnosis] 종료 id={Id} status={Status} tools={Tools} in={In} out={Out} cacheRead={CacheRead} elapsed={Elapsed:0}s shas={Shas}"),
                dto.Id, dto.Status, dto.ToolCalls, dto.InputTokens, dto.OutputTokens, dto.CacheReadTokens,
                sw.Elapsed.TotalSeconds, string.Join(",", dto.SourceShas.Select(kv => $"{kv.Key}@{kv.Value}")));
        }
    }
}

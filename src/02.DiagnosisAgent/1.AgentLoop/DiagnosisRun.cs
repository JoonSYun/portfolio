using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 실행 1건의 상태. 백그라운드 실행·병렬 도구·폴링 조회가 동시에 만지므로 모든 변경은 잠금 안에서 한다.
/// 끝난 상태(성공·거절·실패·취소)는 처음 정해진 것이 유지된다(늦게 온 취소·오류가 결과를 덮지 않게).
/// </summary>
public sealed class DiagnosisRun
{
    private readonly object                 _gate  = new();
    private readonly List<DiagnosisStepDto> _steps = new();

    private string                              _status = DiagnosisRunStatuses.Queued;
    private DateTime?                           _startedAt;
    private DateTime?                           _finishedAt;
    private DiagnosisReport?                    _report;
    private DiagnosisFixProposal?               _fix;
    private DiagnosisDecline?                   _decline;
    private string?                             _finalText;
    private string?                             _error;
    private long                                _input, _output, _cacheRead, _cacheWrite;
    private int                                 _toolCalls;
    private IReadOnlyDictionary<string, string> _shas = new Dictionary<string, string>();
    private string?                             _fixRunId;

    public DiagnosisRun(
        string           kind,
        DiagnosisRequest request,
        string           startedBy,
        string?          parentRunId  = null,
        DiagnosisReport? parentReport = null,
        DateTime?        createdAt    = null)
    {
        Id           = Guid.NewGuid().ToString("N");
        Kind         = kind;
        Request      = request;
        StartedBy    = startedBy;
        ParentRunId  = parentRunId;
        ParentReport = parentReport;
        CreatedAt    = createdAt ?? DatetimeHelper.Now;
    }

    public string           Id           { get; }
    public string           Kind         { get; }
    public DiagnosisRequest Request      { get; }
    public string           StartedBy    { get; }
    public string?          ParentRunId  { get; }
    /// <summary>수정 방안 실행이 넘겨받은 원 진단 보고서.</summary>
    public DiagnosisReport? ParentReport { get; }
    public DateTime         CreatedAt    { get; }
    public CancellationTokenSource Cancellation { get; } = new();

    public string Status { get { lock (_gate) return _status; } }
    public DiagnosisReport? Report { get { lock (_gate) return _report; } }
    public string? FixRunId { get { lock (_gate) return _fixRunId; } }
    public bool IsFinished { get { lock (_gate) return IsFinishedUnsafe; } }

    private bool IsFinishedUnsafe => _status is DiagnosisRunStatuses.Succeeded
                                             or DiagnosisRunStatuses.Rejected
                                             or DiagnosisRunStatuses.Failed
                                             or DiagnosisRunStatuses.Cancelled;

    public void MarkRunning(IReadOnlyDictionary<string, string> sourceShas)
    {
        lock (_gate)
        {
            if (IsFinishedUnsafe) return;
            _status    = DiagnosisRunStatuses.Running;
            _startedAt = DatetimeHelper.Now;
            _shas      = sourceShas;
        }
    }

    public void AddStep(string summary)
    {
        lock (_gate) _steps.Add(new DiagnosisStepDto { At = DatetimeHelper.Now, Summary = summary });
    }

    public void AddUsage(long input, long output, long cacheRead, long cacheWrite)
    {
        lock (_gate) { _input += input; _output += output; _cacheRead += cacheRead; _cacheWrite += cacheWrite; }
    }

    public void SetToolCalls(int count)
    {
        lock (_gate) _toolCalls = count;
    }

    public void LinkFix(string fixRunId)
    {
        lock (_gate) _fixRunId = fixRunId;
    }

    public void SucceedWithReport(DiagnosisReport report) => Finish(DiagnosisRunStatuses.Succeeded, () => _report = report);
    public void SucceedWithFix(DiagnosisFixProposal fix)  => Finish(DiagnosisRunStatuses.Succeeded, () => _fix = fix);
    public void Reject(DiagnosisDecline decline)          => Finish(DiagnosisRunStatuses.Rejected, () => _decline = decline);
    public void Fail(string error, string? finalText = null)
        => Finish(DiagnosisRunStatuses.Failed, () => { _error = error; _finalText = finalText; });
    public void MarkCancelled() => Finish(DiagnosisRunStatuses.Cancelled, () => _error = "취소되었습니다.");

    private void Finish(string status, Action apply)
    {
        lock (_gate)
        {
            if (IsFinishedUnsafe) return;
            apply();
            _status     = status;
            _finishedAt = DatetimeHelper.Now;
        }
    }

    public DiagnosisRunDto ToDto()
    {
        lock (_gate)
            return new DiagnosisRunDto
            {
                Id = Id, Kind = Kind, Status = _status, Request = Request, StartedBy = StartedBy,
                CreatedAt = CreatedAt, StartedAt = _startedAt, FinishedAt = _finishedAt,
                Steps = _steps.ToList(), Report = _report, Fix = _fix, Decline = _decline, FinalText = _finalText, Error = _error,
                InputTokens = _input, OutputTokens = _output, CacheReadTokens = _cacheRead, CacheWriteTokens = _cacheWrite,
                ToolCalls = _toolCalls, SourceShas = _shas, ParentRunId = ParentRunId, FixRunId = _fixRunId,
            };
    }
}

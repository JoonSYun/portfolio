using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Portfolio.Admin.Models;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 실행 기록을 메모리에 보관한다(DB 테이블 없음). 끝난 기록은 보관 시간이 지나면 다음 추가 때 지운다.
/// 서버(앱 풀)가 다시 시작되면 기록은 사라진다. 동시 실행 수는 <see cref="Slots"/> 로 제한한다.
/// </summary>
public sealed class DiagnosisRunStore
{
    private readonly ConcurrentDictionary<string, DiagnosisRun> _runs = new();
    private readonly TimeSpan _retention;

    public DiagnosisRunStore(IOptions<DiagnosisOptions> options)
    {
        var o = options.Value;
        _retention = TimeSpan.FromHours(Math.Max(1, o.RunRetentionHours));
        Slots      = new SemaphoreSlim(Math.Max(1, o.MaxConcurrentRuns));
    }

    public SemaphoreSlim Slots { get; }

    public DiagnosisRun Add(DiagnosisRun run)
    {
        Prune();
        _runs[run.Id] = run;
        return run;
    }

    public DiagnosisRun Get(string id)
        => _runs.TryGetValue(id, out var run)
            ? run
            : throw new NotFoundException("진단 기록을 찾을 수 없습니다. 서버가 다시 시작되면 기록이 사라집니다.");

    public bool TryGet(string id, out DiagnosisRun? run) => _runs.TryGetValue(id, out run);

    private void Prune()
    {
        var cutoff = DatetimeHelper.Now - _retention;
        foreach (var (id, run) in _runs)
            if (run.IsFinished && run.CreatedAt < cutoff)
                _runs.TryRemove(id, out _);
    }
}

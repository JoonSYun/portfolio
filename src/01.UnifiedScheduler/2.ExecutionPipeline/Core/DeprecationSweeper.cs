// [담당업무 2] 마감 단계 보조 — 큐 정체·워커 멎음으로 남은 stale ENQUEUED/RUNNING row 를 매분 정리한다.

using Hangfire;
using Microsoft.Extensions.Options;
using Portfolio.UnifiedScheduler.Data;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// 매분 실행. Filter 가 못 잡는 "큐 정체 / 워커 멎음" 케이스를 SQL 스캔으로 마감.
///   1) ENQUEUED 단계: STATUS='ENQUEUED' AND DEPRECATE_SEC &gt; 0 AND (now - SCHEDULED_DT) &gt; DEPRECATE_SEC
///   2) RUNNING  단계: STATUS='RUNNING'  AND (now - STARTED_UTC)  &gt; TIMEOUT_SEC + grace
/// 1) 은 "시작 전 큐 대기" 판정이라 DEPRECATE_SEC, 2) 는 "시작 후 실행 길이" 판정이라 TIMEOUT_SEC 을
/// 쓴다 — 서로 다른 컬럼인 게 의도된 것이다. DEPRECATE_SEC=0 은 무제한이라 1) 대상에서 빠진다.
/// 1) 의 기준 시각 SCHEDULED_DT 는 DeprecationFilter 가 쓰는 JobArgs.BucketedDt 와 같은 값이라,
/// 두 지점이 "예정 시각으로부터의 나이" 라는 동일한 잣대로 판정한다.
/// grace 는 <see cref="DeprecationSweeperOptions.RunningGraceSeconds"/> (기본 60s) — 정상
/// 종료 직전 row 가 race-loser 로 DEPRECATED 되지 않도록 둔 환경 변동(GC pause·UPDATE 지연) 여유분.
/// <para/>
/// 두 단계는 try/catch 로 분리 — ENQUEUED 단계가 throw 해도 RUNNING 단계는 항상 실행되어
/// 한쪽 SQL 장애가 stale 누적을 막지 않도록 한다 (ScheduleSyncJob 과 동일 패턴).
/// <para/>
/// 마감된 row 는 <see cref="SafeNotify"/> 로 건별 메일 통지된다 — 수신자는 개발팀뿐이며
/// 브랜드 담당자에게는 나가지 않는다.
/// </summary>
public class DeprecationSweeper
{
    private readonly JobLogRepository            _logs;
    private readonly INotifier                   _notifier;
    private readonly DeprecationSweeperOptions   _opt;
    private readonly ILogger<DeprecationSweeper> _logger;

    public DeprecationSweeper(
        JobLogRepository                    logs,
        INotifier                           notifier,
        IOptions<DeprecationSweeperOptions> opt,
        ILogger<DeprecationSweeper>         logger)
    {
        _logs     = logs;
        _notifier = notifier;
        _opt      = opt.Value;
        _logger   = logger;
    }

    public void Run()
    {
        try
        {
            Sweep(_logs.FindStaleEnqueuedJobs(), JobStatus.ENQUEUED);
        }
        catch (Exception ex)
        {
            // 다음 단계(RUNNING sweep)는 막지 않는다.
            _logger.LogError(ex,
                AppLog.Log("DeprecationSweeper: ENQUEUED sweep failed; continuing to RUNNING sweep"));
        }

        try
        {
            Sweep(_logs.FindStaleRunningJobs(_opt.RunningGraceSeconds), JobStatus.RUNNING);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                AppLog.Log("DeprecationSweeper: RUNNING sweep failed"));
        }
    }

    /// <summary>
    /// Hangfire 잡 삭제는 row-by-row 가 불가피하지만 SCH_JOB_LOG 만 건드리지는 않으므로
    /// SCH_JOB_LOG NC index 페이지를 깨우지는 않는다. SCH_JOB_LOG STATUS 전이는 한
    /// statement 로 묶어 NC index (IX_ACTIVE) 페이지 갱신 횟수와 락 보유 시간을 최소화
    /// — Dispatcher SELECT 와의 deadlock 표면 축소.
    /// </summary>
    private void Sweep(IReadOnlyList<StaleJobRow> stale, JobStatus fromStatus)
    {
        if (stale.Count == 0)
        {
            // 정상 운영의 99% 케이스 — 매분 1회 호출되므로 info 로 띄우면 노이즈. debug 로만 heartbeat 남김.
            _logger.LogDebug(
                AppLog.Log("DeprecationSweeper: no stale {FromStatus} jobs"),
                fromStatus);
            return;
        }

        foreach (var r in stale)
        {
            if (!string.IsNullOrEmpty(r.HangfireJobId))
            {
                BackgroundJob.Delete(r.HangfireJobId);
                _logger.LogInformation(
                    AppLog.Log("DeprecationSweeper: deleted Hangfire job {HangfireJobId} for stale {FromStatus} log {LogId}"),
                    r.HangfireJobId, fromStatus, r.LogId);
            }
        }

        // MarkManyDeprecated 반환값(updated) 이 stale.Count 보다 작으면 그 차이만큼 race-loser
        // (다른 경로에서 이미 다른 status 로 전이된 row). updated/total 비율로 로그 한 줄에 표현.
        var updated = _logs.MarkManyDeprecated(stale.Select(r => r.LogId).ToList(), fromStatus);

        _logger.LogInformation(
            AppLog.Log("DeprecationSweeper: deprecated {Updated}/{Total} stale {FromStatus} jobs"),
            updated, stale.Count, fromStatus);

        // 폐기 1건당 메일 1통. ExecuteUpdate 는 어떤 LogId 가 실제로 전이됐는지 돌려주지 않으므로
        // updated < stale.Count 인 드문 race 에서는 이미 다른 경로로 마감된 row 도 메일이 나갈 수 있다 —
        // 그 폭은 위 로그의 updated/total 로 확인 가능하다.
        foreach (var r in stale)
            SafeNotify(r, fromStatus);
    }

    /// <summary>
    /// 폐기 사실을 개발팀에 메일 통지. <see cref="INotifier"/> 는 알림 전용 큐로 enqueue 만 하므로
    /// 스윕 루프가 SMTP 응답을 기다리지 않는다. 발송 자체의 예외는 swallow — 한 건의 통지 실패가
    /// 나머지 stale row 의 통지를 막지 않는다.
    /// </summary>
    /// <remarks>
    /// <see cref="DeprecationFilter"/> 의 통지와 두 가지가 다르다.
    /// <list type="bullet">
    ///   <item><c>SystemMailSend=true</c> 고정 — 메일 게이트는 enqueue 시점 <see cref="JobArgs"/> 스냅샷에만 있고
    ///         SCH_JOB_LOG 에는 컬럼이 없어 복원할 수 없다. 스위퍼가 잡는 건 "필터조차 못 걸러낸" 상황이라
    ///         도메인 MAIL_SEND 와 무관하게 개발팀에는 알리는 쪽을 택했다.</item>
    ///   <item>단계별 사유가 갈린다 — ENQUEUED 는 큐 대기 한도(DEPRECATE_SEC) 초과, RUNNING 은 실행 한도
    ///         (TIMEOUT_SEC + grace) 초과로 워커가 멎은 것으로 판정된 케이스다.</item>
    /// </list>
    /// </remarks>
    private void SafeNotify(StaleJobRow r, JobStatus fromStatus)
    {
        try
        {
            var message = fromStatus == JobStatus.ENQUEUED
                ? $"Queue wait limit exceeded — scheduled={r.ScheduledDt:yyyy-MM-dd HH:mm:ss}, deprecate={r.DeprecateSec}s. "
                + "No worker started the job in time; swept before execution."
                : $"Execution limit exceeded — started={r.StartedDt:yyyy-MM-dd HH:mm:ss}, timeout={r.TimeoutSec}s "
                + $"(+{_opt.RunningGraceSeconds}s grace). Worker appears stalled; job was force-closed.";

            _notifier.Send(new NotificationRequest
            {
                Subject    = $"[{r.DomainCode}/{r.BrandCode}] Job deprecated by sweeper ({fromStatus})",
                JobName    = nameof(DeprecationSweeper),
                DomainCode = r.DomainCode,
                BrandCode  = r.BrandCode,
                CenterCode = r.CenterCode ?? "",
                LogId      = r.LogId,
                Requester  = r.Requester,
                IsTest     = r.TestYn == "Y",
                ErrorType  = "Deprecated",
                IsBusiness = false,
                Message    = $"{message} HangfireJobId={r.HangfireJobId}",
                SystemMailSend = true,
                BrandMailSend  = false,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                AppLog.Log("DeprecationSweeper: Notifier.Send threw for {LogId} ({FromStatus}); mail skipped"),
                r.LogId, fromStatus);
        }
    }
}

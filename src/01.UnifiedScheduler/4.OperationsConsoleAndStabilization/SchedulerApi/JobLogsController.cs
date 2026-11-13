using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.UnifiedScheduler.Data;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Controllers;

/// <summary>
/// Hangfire 연동이 필요한 잡 로그 액션만 남는다 (수동 deprecate). 조회 endpoint(GET /api/admin/job-logs)
/// 는 Portfolio.Admin 솔루션으로 이전되어 그쪽 DashboardRepository / JobLogReadRepository 가 처리한다.
/// </summary>
[ApiController]
[Route("api/admin/job-logs")]
[Authorize] // 내부 전용. Admin 백엔드 프록시가 포워딩한 사용자 JWT 를 검증한다.
public class JobLogsController : ControllerBase
{
    private readonly JobLogRepository           _repo;
    private readonly ILogger<JobLogsController> _logger;

    public JobLogsController(JobLogRepository repo, ILogger<JobLogsController> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    /// <summary>
    /// 운영자가 ENQUEUED/RUNNING 잡을 즉시 종료. <see cref="Jobs.DeprecationSweeper"/> (매분 1회) 를
    /// 기다리지 못하는 운영 상황을 위한 escape hatch.
    ///
    /// <para>흐름:</para>
    /// <list type="number">
    /// <item><description><see cref="JobLogRepository.ValidateForDeprecate"/> — row 존재 + 현재 status 검증 (없으면 404, terminal 이면 409).</description></item>
    /// <item><description>HangfireJobId 가 있으면 <see cref="InspectHangfireTerminal"/> 로 Hangfire 측 종료 상태 검사.
    ///   Succeeded/Failed/Deleted 면 DB row 가 stale — Hangfire 실제 결과(SUCCESS/FAILED/DEPRECATED) 로 sync 해 정합성 복구.</description></item>
    /// <item><description>Hangfire 활성/없음 → DB 를 DEPRECATED 로 묻고 큐에서도 best-effort 제거.</description></item>
    /// </list>
    ///
    /// <para>
    /// 주의: RUNNING 잡에 대해 <see cref="BackgroundJob.Delete(string)"/> 는 워커가 들고 있는 작업을
    /// 즉시 멈추지 못한다. 워커가 <c>IJobCancellationToken</c> 을 보면 취소되고, 안 보면 끝까지 마치되
    /// 결과 콜백(SUCCESS/FAILED) 은 from-status 가드에 의해 무시된다 — DB 정합성은 유지된다.
    /// </para>
    /// </summary>
    [HttpPost("{logId}/deprecate")]
    public ActionResult<ApiResponse<JobLogDeprecateResultDto>> Deprecate(string logId)
    {
        // [1] row 검증. NotFoundException(404) / BusinessRuleException(409) 은 전역 핸들러로 ProblemDetails 화.
        var preview = _repo.ValidateForDeprecate(logId);

        // [2] HangfireJobId 가 있으면 Hangfire 측 상태 검사. 종료(Succeeded/Failed/Deleted) 면 DB row 가 stale
        //     이라는 신호이므로 운영자 의도(=종료) 와 별개로 Hangfire 실제 결과를 진실로 받아 sync.
        if (!string.IsNullOrEmpty(preview.HangfireJobId))
        {
            var hf = InspectHangfireTerminal(preview.HangfireJobId);
            if (hf.HasValue)
            {
                _repo.TransitionFromActiveGuarded(logId, hf.Value.DbStatus, hf.Value.ErrorMessage);
                _logger.LogInformation(
                    AppLog.Log("Deprecate: synced from Hangfire — logId={LogId} hangfireState={State} → dbStatus={DbStatus}"),
                    logId, hf.Value.HangfireStateName, hf.Value.DbStatus);

                return Ok(ApiResponse<JobLogDeprecateResultDto>.OkMessage(
                    new JobLogDeprecateResultDto
                    {
                        Outcome           = "SyncedFromHangfire",
                        FinalStatus       = hf.Value.DbStatus,
                        HangfireStateName = hf.Value.HangfireStateName,
                    },
                    $"Synced from Hangfire ({hf.Value.HangfireStateName} → {hf.Value.DbStatus})",
                    HttpContext));
            }
        }

        // [3] Hangfire 활성/모름/없음 → 일반 manual deprecate.
        var updated = _repo.TransitionFromActiveGuarded(logId, JobStatus.DEPRECATED, null);

        // DB 전이가 우리 호출에서 성공한 경우만 Hangfire 큐 정리 시도. race-loser 케이스(sweeper/워커가 먼저
        // 전이) 는 Hangfire 도 이미 정리되어 있을 가능성이 높아 중복 호출 회피.
        if (updated && !string.IsNullOrEmpty(preview.HangfireJobId))
        {
            try
            {
                BackgroundJob.Delete(preview.HangfireJobId);
            }
            catch (Exception ex)
            {
                // 큐 정리 실패해도 DB 는 이미 DEPRECATED 이고 DeprecationFilter 가 워커 pickup 시점에 한 번 더 막아준다.
                _logger.LogWarning(ex,
                    AppLog.Log("Hangfire.Delete failed after manual deprecate: logId={LogId} hangfireJobId={HangfireJobId}"),
                    logId, preview.HangfireJobId);
            }
        }

        return Ok(ApiResponse<JobLogDeprecateResultDto>.OkMessage(
            new JobLogDeprecateResultDto
            {
                Outcome     = updated ? "Deprecated" : "AlreadyTransitioned",
                FinalStatus = JobStatus.DEPRECATED,
            },
            updated ? $"Job deprecated: {logId}" : $"Job already transitioned: {logId}",
            HttpContext));
    }

    /// <summary>
    /// Hangfire <see cref="IMonitoringApi.JobDetails(string)"/> 로 잡의 현재 state 를 확인하고, 종료 상태
    /// (Succeeded/Failed/Deleted) 면 그에 대응하는 DB <see cref="JobStatus"/> 와 (Failed 인 경우) 예외 메시지를
    /// 묶어 반환. 활성(Enqueued/Processing/Scheduled/Awaiting) 또는 storage 에 없는 경우 null.
    ///
    /// <para>
    /// Hangfire <see cref="JobDetailsDto"/> 의 <c>History</c> 는 신규 → 과거 순(역시간순) 으로 정렬돼 있지만,
    /// 정렬 가정에 의존하지 않기 위해 <c>CreatedAt</c> 으로 명시 재정렬한 뒤 가장 최근 state 만 사용한다.
    /// </para>
    /// </summary>
    private (JobStatus DbStatus, string HangfireStateName, string? ErrorMessage)? InspectHangfireTerminal(string hangfireJobId)
    {
        JobDetailsDto? details;
        try
        {
            details = JobStorage.Current.GetMonitoringApi().JobDetails(hangfireJobId);
        }
        catch (Exception ex)
        {
            // Hangfire storage 호출 실패 — 활성으로 간주하고 일반 deprecate 경로로 떨어지게 한다.
            _logger.LogWarning(ex,
                AppLog.Log("Hangfire.JobDetails failed: hangfireJobId={HangfireJobId}"),
                hangfireJobId);
            return null;
        }

        if (details?.History == null || details.History.Count == 0)
            return null;  // storage retention 만료 등 — 활성으로 간주.

        var latest = details.History.OrderByDescending(h => h.CreatedAt).First();
        return latest.StateName switch
        {
            "Succeeded" => (JobStatus.SUCCESS,    "Succeeded", null),
            "Failed"    => (JobStatus.FAILED,     "Failed",    ExtractFailureMessage(latest)),
            "Deleted"   => (JobStatus.DEPRECATED, "Deleted",   null),
            _           => null,  // Enqueued / Processing / Scheduled / Awaiting → 활성으로 간주.
        };
    }

    /// <summary>
    /// Hangfire <c>FailedState</c> 의 Data 사전에서 ExceptionType + ExceptionMessage 를 추출.
    /// JobLogRepository.FormatErrorMessage 와 같은 <c>[Type] Message</c> 포맷을 유지해 운영자가
    /// 워커 catch 로 들어온 row 와 Hangfire-sync row 를 같은 화면에서 일관되게 읽도록 한다.
    /// </summary>
    private static string? ExtractFailureMessage(StateHistoryDto state)
    {
        if (state.Data == null) return null;
        state.Data.TryGetValue("ExceptionType",    out var type);
        state.Data.TryGetValue("ExceptionMessage", out var msg);
        if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(msg)) return $"[{type}] {msg}";
        return msg ?? type;
    }
}

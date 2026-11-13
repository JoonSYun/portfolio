// [담당업무 2] 등록·동기화 단계 — DB 스케줄 설정(원본)을 Hangfire RecurringJob(파생)으로 매분 단방향 복제한다.

using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Options;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// 매분 실행. SCH_DOMAIN_SCHEDULE(원본) → HangFire.Hash(파생) 단방향 복제.
///
/// 두 단계로 구성되며 try/catch 로 분리한다 — 한쪽이 throw 해도 다른 쪽은 항상 실행되어야
/// 사이클 안에 사용/비활성/삭제가 모두 1분 내 반영됨이 보장된다.
/// <list type="number">
///   <item>cron 식 in-place 편집 반영: <c>UPD_DT &gt; SYNCED_UPD_DT</c> 인 row → <c>RecurringJob.AddOrUpdate</c></item>
///   <item>등록셋 ↔ 활성셋 reconcile: 양방향 차집합으로 <c>RemoveIfExists</c>(dead) + <c>AddOrUpdate</c>(missing)</item>
/// </list>
///
/// <para>
/// <b>1·2단계의 역할 분담</b> — 1단계는 "이미 등록된 잡의 cron 식이 바뀐 경우"만 책임진다. cron 변경은
/// 활성 여부를 바꾸지 않으므로(등록 ↔ 등록) set-difference 로는 잡히지 않아, <see cref="DomainScheduleRow.UpdDt"/>
/// 체크포인트로만 감지 가능하다. 그 외 "활성 여부 자체의 변화"는 전부 2단계가 처리한다.
/// </para>
/// <para>
/// <b>2단계가 양방향 set-difference 인 이유</b> — 도메인의 활성 여부는 여러 테이블에 흩어져 있다:
/// SCH_DOMAIN.USE_YN / SCH_DOMAIN_GROUP.USE_YN / SCH_DOMAIN_GROUP_MAP 매핑 / SCH_DOMAIN_SCHEDULE.USE_YN /
/// VALID_FROM·VALID_TO. 이 중 변경 체크포인트(SYNCED_UPD_DT)가 있는 테이블은 SCH_DOMAIN_SCHEDULE 하나뿐이라,
/// 다른 테이블이 활성 여부를 바꿔도 1단계의 UPD_DT 트리거는 울리지 않는다. 특히 VALID 날짜 도래는 쓰기
/// 이벤트조차 없어(시간 경과) 어떤 UPD_DT 로도 감지 불가능하다. 따라서 "지금 활성인 집합"을 매 tick 라이브로
/// 조회해 Hangfire 등록 집합과 대조(reconcile)하는 것만이 모든 경우를 수렴시킨다.
/// </para>
/// <list type="bullet">
///   <item><b>dead</b> (등록 − 활성 − 인프라): SCH_DOMAIN row 삭제 / 스케줄·도메인·그룹 USE_YN='N' /
///         그룹 매핑 제거 / VALID 만료 → 제거.</item>
///   <item><b>missing</b> (활성 − 등록 − 인프라): 그룹 USE_YN N→Y / 그룹 매핑 추가 / VALID 도래 등
///         UPD_DT 를 건드리지 않는 모든 재활성화 → 재등록. (구버전의 비대칭 버그 — 제거는 set-difference,
///         등록은 UPD_DT 트리거 — 로 인해 그룹 토글 후 영영 재등록되지 않던 결함을 해소.)</item>
/// </list>
/// </summary>
public class ScheduleSyncJob
{
    private readonly ScheduleRepository      _repo;
    private readonly ILogger<ScheduleSyncJob> _logger;
    private readonly string                   _recurringQueue;

    public ScheduleSyncJob(
        ScheduleRepository       repo,
        ILogger<ScheduleSyncJob> logger,
        IOptions<HangfireOptions> options)
    {
        _repo           = repo;
        _logger         = logger;
        _recurringQueue = options.Value.Recurring.QueueName;
    }

    public void Run()
    {
        // ─── 1) cron 식 in-place 편집 반영 (UPD_DT 체크포인트) ───────────────
        // 이미 등록된 잡의 cron 변경은 활성셋이 그대로라 2단계 reconcile 로는 안 잡힌다 → UPD_DT 로만 감지.
        try
        {
            var changed = 0;
            foreach (var d in _repo.GetChangedSchedules())
            {
                Register(d);
                _repo.MarkSynced(d.DomainCode, d.UpdDt);
                changed++;
            }
            _logger.LogInformation(
                AppLog.Log("ScheduleSync: changed step OK ({Count} domains synced)"),
                changed);
        }
        catch (Exception ex)
        {
            // 다음 단계(reconcile)는 막지 않는다.
            _logger.LogError(ex,
                AppLog.Log("ScheduleSync: changed step failed; continuing to reconcile"));
        }

        // ─── 2) 등록셋 ↔ 활성셋 양방향 reconcile ───────────────────────────
        //
        // (a) DB 진실: 지금 Hangfire 에 등록되어 있어야 할 활성 도메인 전체 (cron/큐/tz 포함 full row).
        //     활성 정의는 ScheduleRepository.GetActiveDomainSchedules 가 단일 source.
        // (b) Hangfire 진실: [HangFire].[Set] "recurring-jobs" 에 박혀있는 ID 전체 (인프라 잡 제외).
        //
        // dead   = (b) − (a) : 비활성화된 도메인 → 제거.
        // missing = (a) − (b) : 활성인데 미등록(그룹 N→Y / 매핑 추가 / VALID 도래 등) → 재등록.
        try
        {
            var active     = _repo.GetActiveDomainSchedules();
            var activeById = active.ToDictionary(d => d.DomainCode, StringComparer.Ordinal);

            using var conn = JobStorage.Current.GetConnection();
            var registered = conn.GetRecurringJobs()
                .Select(r => r.Id)
                .Where(id => !InfraRecurringJobIds.All.Contains(id))
                .ToHashSet(StringComparer.Ordinal);

            // (a) dead 제거 — 등록돼 있으나 더 이상 활성이 아닌 도메인.
            var deads = registered.Where(id => !activeById.ContainsKey(id)).ToList();
            foreach (var dead in deads)
            {
                RecurringJob.RemoveIfExists(dead);
                _logger.LogDebug(
                    AppLog.Log("ScheduleSync: removed recurring job '{Job}'"),
                    dead);
            }

            // (b) missing 추가 — 활성이지만 Hangfire 에 등록되지 않은 도메인.
            var missing = activeById.Keys.Where(id => !registered.Contains(id)).ToList();
            foreach (var id in missing)
            {
                Register(activeById[id]);
                _logger.LogDebug(
                    AppLog.Log("ScheduleSync: re-added recurring job '{Job}'"),
                    id);
            }

            _logger.LogInformation(
                AppLog.Log("ScheduleSync: reconcile OK (removed {Dead} [{Deads}], added {Missing} [{Missings}])"),
                deads.Count, string.Join(",", deads),
                missing.Count, string.Join(",", missing));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, AppLog.Log("ScheduleSync: reconcile failed"));
        }
    }

    /// <summary>
    /// 도메인 RecurringJob 1건을 Hangfire 에 등록/갱신한다. 1단계(변경 반영)·2단계(missing 재등록) 공용.
    /// </summary>
    /// <param name="d">등록 대상 도메인의 스케줄 row (cron 식 포함).</param>
    /// <remarks>
    /// RecurringJob 본체는 항상 recurring 전용 큐(<see cref="_recurringQueue"/>)로 발사한다.
    /// 카테고리 큐(<see cref="DomainScheduleRow.DefaultQueue"/>)는 <see cref="Dispatcher.Fire"/> 내부에서
    /// 자식 BackgroundJob 의 EnqueuedState 로만 쓰인다. KST 단일 시간대 정책에 따라
    /// <see cref="DomainScheduleRow.TimezoneId"/> 는 무시하고 항상 "Korea Standard Time" 으로 평가한다.
    /// </remarks>
    private void Register(DomainScheduleRow d)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

        RecurringJob.AddOrUpdate<Dispatcher>(
            recurringJobId: d.DomainCode,
            queue:          _recurringQueue,
            methodCall:     x => x.Fire(d.DomainCode, null),
            cronExpression: d.CronExpr,
            options:        new RecurringJobOptions { TimeZone = tz });
    }
}

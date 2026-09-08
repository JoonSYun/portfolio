namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// 인프라 RecurringJob 3종의 ID 단일 진실 — 등록(<see cref="RecurringJobsExtensions"/>) 와
/// dead 정리(<see cref="Jobs.ScheduleSyncJob"/>) 양쪽이 같은 상수를 참조해 사일런트 drift 를 차단한다.
/// </summary>
/// <remarks>
/// <see cref="All"/> 셋은 dead 정리의 보호 목록. 새 인프라 잡을 추가하면 반드시 여기에도 등록할 것 —
/// 누락 시 등록 후 다음 sync 사이클에 즉시 삭제되므로 실수가 빠르게 드러난다.
/// 도메인 코드와 충돌 회피를 위해 모두 <c>_</c> 로 시작 (도메인 코드 작명 규칙은 영문 대문자/숫자).
/// </remarks>
public static class InfraRecurringJobIds
{
    public const string ScheduleSync       = "_schedule_sync";
    public const string DeprecationSweeper = "_deprecation_sweeper";
    public const string JobLogArchive      = "_jobLog_archive";

    /// <summary>dead 정리 시 보호할 ID 집합. <see cref="Jobs.ScheduleSyncJob"/> 가 set-difference 의 제외 항목으로 사용.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ScheduleSync,
        DeprecationSweeper,
        JobLogArchive,
    };
}

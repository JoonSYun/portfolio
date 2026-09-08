namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// usp_Sch_FetchActiveTargets 프로시저 결과 1행.
/// L2 오버라이드가 L1을 상속한 최종 형태로 내려옴.
/// </summary>
public class TargetRow
{
    public string  DomainCode       { get; set; } = "";
    public string  BrandCode        { get; set; } = "";
    public string  CenterCode       { get; set; } = "";
    public string? PayloadJson      { get; set; }
    public int     TimeoutSec       { get; set; }

    /// <summary>
    /// 큐 대기 폐기 한도(초). <see cref="TimeoutSec"/> 이 "시작된 후" 실행 한도인 것과 달리
    /// 이 값은 "시작되기 전" 대기 한도다. <c>0</c> = 무제한(폐기 안 함).
    /// </summary>
    public int     DeprecateSec     { get; set; }

    public string  TimezoneId       { get; set; } = "Korea Standard Time";

    /// <summary>
    /// 이 브랜드의 활성 시간대 cron 목록 (SCH_DOMAIN_BRAND_CRON, USE_YN='Y' + VALID 범위 today).
    /// 비어있으면 도메인 L1 스케줄을 상속(매 도메인 tick 발사). 1개 이상이면 그 중 이번 tick 에
    /// 매치되는 cron 이 하나라도 있을 때(OR) 발사. 수동 실행 경로는 cron narrowing 을 무시하므로 빈 목록.
    /// </summary>
    public IReadOnlyList<string> CronExprs { get; set; } = Array.Empty<string>();

    public string  JobType          { get; set; } = "";

    /// <summary>
    /// Resolved queue name. SCH_DOMAIN_BRAND_OVERRIDE.QUEUE 가 설정돼 있으면 그 값,
    /// 아니면 SCH_DOMAIN.DEFAULT_QUEUE — Dispatcher 가 그대로 EnqueuedState 에 주입.
    /// 필드 이름은 호환을 위해 유지.
    /// </summary>
    public string  DefaultQueue     { get; set; } = "default";

    /// <summary>
    /// 실효 IsTest. Domain.TEST_YN='Y' OR Brand.TEST_YN='Y' OR (매핑된 그룹 중 TEST_YN='Y' 가 1개 이상).
    /// ScheduleRepository 의 3개 조회 메서드(GetActiveBrandTargets/GetTargetForManualRun/
    /// GetTargetsForManualDomainRun) 가 SQL CASE 로 한 번에 계산해서 채운다.
    /// 수동 실행 경로는 Dispatcher 가 이 값과 RunAsMode 를 OR 결합해 JobArgs.IsTest 로 흘림.
    /// </summary>
    public bool    IsTest           { get; set; }

    /// <summary>
    /// 시스템(개발팀) 메일 게이트 — SCH_DOMAIN.MAIL_SEND='Y' 만. ScheduleRepository 가 채운다.
    /// </summary>
    public bool    SystemMailSend   { get; set; }

    /// <summary>
    /// 브랜드 담당자 메일 게이트 — SCH_DOMAIN.MAIL_SEND='Y' AND SCH_BRAND.MAIL_SEND='Y' AND
    /// SCH_DOMAIN_BRAND_OVERRIDE.MAIL_SEND='Y' (3-레벨 AND). ScheduleRepository 가 채운다.
    /// </summary>
    public bool    BrandMailSend    { get; set; }
}

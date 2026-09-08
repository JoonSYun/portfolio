namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// <see cref="Jobs.DeprecationSweeper"/> 가 stale 판정한 SCH_JOB_LOG row 1건의 투영.
/// </summary>
/// <remarks>
/// LogId/HangfireJobId 외의 필드는 폐기 알림 메일 본문 구성용이다 — 스위퍼는 <see cref="JobArgs"/> 를
/// 받지 않아 도메인/브랜드 컨텍스트를 이 투영으로만 알 수 있다. 같은 row 에서 그대로 읽으므로
/// JOIN 없이 컬럼만 더 가져오는 비용뿐이다.
/// <para>
/// 단 메일 게이트(<see cref="JobArgs.SystemMailSend"/>/<see cref="JobArgs.BrandMailSend"/>) 는 enqueue 시점
/// JobArgs 스냅샷에만 있고 SCH_JOB_LOG 에는 컬럼 자체가 없어 복원할 수 없다 — 스위퍼 알림이 도메인
/// MAIL_SEND 와 무관하게 항상 개발팀으로 나가는 이유.
/// </para>
/// </remarks>
public class StaleJobRow
{
    public string  LogId         { get; set; } = "";
    public string? HangfireJobId { get; set; }

    public string  DomainCode    { get; set; } = "";
    public string  BrandCode     { get; set; } = "";
    public string? CenterCode    { get; set; }

    /// <summary>ENQUEUED 단계 판정 기준 시각 — 잡이 돌기로 "예정됐던" 시각.</summary>
    public DateTime  ScheduledDt { get; set; }

    /// <summary>RUNNING 단계 판정 기준 시각. ENQUEUED 단계 row 에서는 null.</summary>
    public DateTime? StartedDt   { get; set; }

    /// <summary>ENQUEUED 단계의 한도(큐 대기). 0 = 무제한이라 애초에 stale 대상이 아니다.</summary>
    public int DeprecateSec { get; set; }

    /// <summary>RUNNING 단계의 한도(시작 후 실행).</summary>
    public int TimeoutSec   { get; set; }

    public string  TestYn    { get; set; } = "N";
    public string? Requester { get; set; }
}

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// <see cref="Jobs.DeprecationSweeper"/> 동작 옵션. appsettings.json 의
/// "DeprecationSweeper" 섹션과 매핑.
/// </summary>
public class DeprecationSweeperOptions
{
    /// <summary>
    /// RUNNING 잡을 stale 로 판정하기 전 추가로 허용할 grace period (초).
    /// stale 조건: <c>now - STARTED_UTC &gt; TIMEOUT_SEC + RunningGraceSeconds</c>.
    /// TIMEOUT_SEC 자체는 비즈니스 SLA (SOAP 호출 한도 등) 에 맞춰진 값이므로,
    /// 워커 GC pause · DB UPDATE 지연 등 환경 변동으로 살짝 초과한 정상 종료
    /// 케이스가 race-loser 로 DEPRECATED 되지 않도록 여유분을 둔다. 기본 60 초.
    /// <para>
    /// <b>RUNNING 전용</b> — ENQUEUED(큐 대기) 판정은 DEPRECATE_SEC 를 그대로 쓰며 grace 를 더하지 않는다.
    /// </para>
    /// </summary>
    public int RunningGraceSeconds { get; set; } = 60;
}

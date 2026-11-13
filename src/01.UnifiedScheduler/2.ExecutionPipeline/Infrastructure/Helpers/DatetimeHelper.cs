namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// 솔루션 전역에서 현재 시각을 가져오는 단일 진입점.
/// 본 솔루션은 KST(Asia/Seoul) 단일 타임존으로 운영하므로 기본은 <see cref="Now"/> 다 — 직접
/// <c>DateTime.Now</c> / <c>DateTime.UtcNow</c> 를 호출하지 말고 이 헬퍼를 통해 조회한다 (테스트용
/// 클럭 주입이나 타임존 정책 변경이 필요할 때 한 곳만 손대면 되도록).
/// SCH_*_SCHEDULE.TIMEZONE_ID 컬럼은 스키마상 보존하지만 cron 평가 / 시각 비교에서는 사용하지 않는다 —
/// 모든 cron / age 산수는 서버 로컬(KST) 기준으로 일관 처리.
/// </summary>
public static class DatetimeHelper
{
    public static DateTime Now    => DateTime.Now;
    public static DateTime UtcNow => DateTime.UtcNow;
}

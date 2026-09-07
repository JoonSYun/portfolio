namespace Portfolio.UnifiedScheduler.ConfigurationInheritance;

/// <summary>
/// [담당업무 1] 설정으로 확장하는 구조 — 2계층 상속 모델의 상위 계층.
///
/// "변하지 않는 실행 규칙"은 도메인 단위로 한 번만 정의한다.
/// 브랜드가 34개, 도메인이 75개로 늘어도 스케줄 등록 단위는 도메인 하나다.
/// 브랜드별로 달라지는 값은 <see cref="BrandScheduleOverride"/>가 필드 단위로 덮어쓴다.
/// </summary>
public sealed class DomainSchedule
{
    public required string DomainCode { get; init; }          // 예: RETURN_PICKUP, ORDER_SYNC
    public required string GroupCode { get; init; }           // 운영 제어 단위(그룹)
    public required string CronExpression { get; init; }      // 도메인 기본 주기
    public required string QueueName { get; init; }           // 큐 = 워커 서버 단위 (장애 격리 경계)

    public int TimeoutSeconds { get; init; } = 300;
    public int MaxRetry { get; init; } = 3;
    public bool Enabled { get; init; } = true;

    /// <summary>도메인 기본 파라미터. 브랜드 오버라이드와 얕은 병합된다.</summary>
    public Dictionary<string, string> Parameters { get; init; } = new();

    /// <summary>이 도메인이 발사 단계에서 fan-out 할 브랜드 목록.</summary>
    public List<string> BrandCodes { get; init; } = new();
}

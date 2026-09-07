namespace Portfolio.UnifiedScheduler.ConfigurationInheritance;

/// <summary>
/// [담당업무 1] 2계층 상속 모델의 하위 계층 — 브랜드별 오버라이드.
///
/// 모든 필드가 nullable 이다. null = "도메인 기본값을 상속한다".
/// 브랜드는 실제로 다른 값만 적는다. 설정 전체를 복사하지 않는다.
/// 이 구조 덕분에 신규 브랜드 대응은 이 테이블에 행 하나를 넣는 것으로 끝난다
/// (운영 중 280건의 브랜드별 설정을 무배포로 운영).
/// </summary>
public sealed class BrandScheduleOverride
{
    public required string DomainCode { get; init; }
    public required string BrandCode { get; init; }

    public string? CronExpression { get; init; }
    public int? TimeoutSeconds { get; init; }
    public int? MaxRetry { get; init; }
    public bool? Enabled { get; init; }

    /// <summary>덮어쓰거나 추가할 파라미터 키만 담는다.</summary>
    public Dictionary<string, string>? ParameterOverrides { get; init; }
}

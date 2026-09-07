namespace Portfolio.UnifiedScheduler.ConfigurationInheritance;

/// <summary>
/// 도메인 기본값 위에 브랜드 오버라이드를 얹은 "실효 설정".
/// 실행 파이프라인은 이 타입만 본다 — 상속 규칙이 어디서 어떻게 적용됐는지는 알 필요가 없다.
/// </summary>
public sealed record EffectiveSchedule(
    string DomainCode,
    string BrandCode,
    string GroupCode,
    string QueueName,
    string CronExpression,
    int TimeoutSeconds,
    int MaxRetry,
    bool Enabled,
    IReadOnlyDictionary<string, string> Parameters);

namespace Portfolio.UnifiedScheduler.ConfigurationInheritance;

/// <summary>
/// [담당업무 1] 2계층 상속 병합 규칙. 상속 로직은 이 클래스 한 곳에만 존재한다.
///
///   EffectiveSchedule = DomainSchedule  ⊕  BrandScheduleOverride
///     · 스칼라 필드 : override ?? default            (필드 단위 상속)
///     · Parameters  : default 위에 override 를 얕은 병합 (키 단위 상속)
/// </summary>
public sealed class ScheduleConfigResolver
{
    private readonly IScheduleConfigRepository _repository;

    public ScheduleConfigResolver(IScheduleConfigRepository repository) => _repository = repository;

    /// <summary>특정 (도메인, 브랜드) 의 실효 설정.</summary>
    public async Task<EffectiveSchedule> ResolveAsync(string domainCode, string brandCode, CancellationToken ct)
    {
        var domain = await _repository.GetDomainAsync(domainCode, ct)
                     ?? throw new KeyNotFoundException($"도메인 설정 없음: {domainCode}");
        var overrides = await _repository.GetBrandOverridesAsync(domainCode, ct);
        return Merge(domain, brandCode, overrides.FirstOrDefault(o => o.BrandCode == brandCode));
    }

    /// <summary>
    /// 발사(Fire) 단계에서 호출 — 도메인 하나를 브랜드 전체로 fan-out 한다.
    /// 등록 비용은 도메인 수, 실행 단위는 브랜드 수.
    /// </summary>
    public async Task<IReadOnlyList<EffectiveSchedule>> ResolveAllBrandsAsync(string domainCode, CancellationToken ct)
    {
        var domain = await _repository.GetDomainAsync(domainCode, ct)
                     ?? throw new KeyNotFoundException($"도메인 설정 없음: {domainCode}");
        var overrides = (await _repository.GetBrandOverridesAsync(domainCode, ct))
            .ToDictionary(o => o.BrandCode);

        return domain.BrandCodes
            .Select(brand => Merge(domain, brand, overrides.GetValueOrDefault(brand)))
            .ToList();
    }

    private static EffectiveSchedule Merge(DomainSchedule d, string brandCode, BrandScheduleOverride? o)
    {
        var parameters = new Dictionary<string, string>(d.Parameters);
        if (o?.ParameterOverrides is { } po)
            foreach (var (key, value) in po) parameters[key] = value;

        return new EffectiveSchedule(
            DomainCode: d.DomainCode,
            BrandCode: brandCode,
            GroupCode: d.GroupCode,
            QueueName: d.QueueName,
            CronExpression: o?.CronExpression ?? d.CronExpression,
            TimeoutSeconds: o?.TimeoutSeconds ?? d.TimeoutSeconds,
            MaxRetry: o?.MaxRetry ?? d.MaxRetry,
            Enabled: o?.Enabled ?? d.Enabled,
            Parameters: parameters);
    }
}

namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// 한 도메인의 브랜드별 활성잡 lookup. <see cref="JobLogRepository.GetActiveJobsByBrand"/> 가
/// 한 round-trip 으로 채워서 반환하고, Dispatcher 가 fan-out 루프에서 O(1) 조회용으로 사용한다.
/// 같은 BrandCode 에 활성 row 가 여러 개면 가장 최근 ENQUEUED 한 건만 남긴다 (단일 호출
/// <see cref="JobLogRepository.GetActiveJob"/> 와 동일 시멘틱).
/// </summary>
public sealed class ActiveJobMap
{
    private readonly Dictionary<string, ActiveJobRow> _byBrand;

    /// <param name="rows">EnqueuedDt DESC 로 미리 정렬된 row 시퀀스. 같은 BrandCode 가
    /// 여러 번 등장하면 첫 항목이 우선.</param>
    public ActiveJobMap(IEnumerable<ActiveJobRow> rows)
    {
        _byBrand = new Dictionary<string, ActiveJobRow>();
        foreach (var r in rows)
            _byBrand.TryAdd(r.BrandCode, r);
    }

    public static ActiveJobMap Empty { get; } = new(Array.Empty<ActiveJobRow>());

    public int Count => _byBrand.Count;

    /// <summary>해당 브랜드의 활성잡이 있으면 true + <paramref name="row"/> 채워서 반환.</summary>
    public bool TryGet(string brandCode, out ActiveJobRow row)
        => _byBrand.TryGetValue(brandCode, out row!);
}

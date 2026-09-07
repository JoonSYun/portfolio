namespace Portfolio.IntegrationPlatform.RuleConfiguration;

/// <summary>
/// [담당업무 2] 업무 규칙의 설정화 — 설정 값의 세 가지 형태를 하나의 스키마로 수용.
///
/// 브랜드·거래처마다 달라지는 분기(배송 방식 코드, 허용 창고, SKU 매핑…)를 코드의 if/switch 에서
/// 걷어내 설정으로 외부화했다. 실무에서 필요한 값의 형태는 결국 세 종류였다:
///   · SingleValue : "cutoffHour" = "14"
///   · List        : "AllowedWarehouses" = [W01, W02]
///   · CodeMap     : "ShipMethod" = { "01"→"PARCEL", "02"→"QUICK" }
/// 세 종류를 한 테이블(rule.ConfigEntry)에 담고, 스코프(브랜드/센터/서비스)별 스냅샷으로 꺼낸다.
/// </summary>
public enum RuleValueKind { SingleValue, List, CodeMap }

/// <summary>설정 한 줄. Scope 가 넓을수록 기본값, 좁을수록 오버라이드.</summary>
public sealed class RuleConfigEntry
{
    public long Id { get; set; }
    public required string ConnectorCode { get; set; }
    public required string Key { get; set; }
    public required RuleValueKind Kind { get; set; }

    // 스코프 — null 은 "모든 것에 적용(기본값)"
    public string? BrandCode { get; set; }
    public string? CenterCode { get; set; }
    public string? ServiceCode { get; set; }

    /// <summary>Kind 에 따라 문자열 / JSON 배열 / JSON 객체.</summary>
    public required string ValueJson { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>스코프 특이도 — 높을수록 우선. (서비스 > 센터 > 브랜드 > 전역)</summary>
    public int Specificity =>
        (BrandCode is null ? 0 : 1) + (CenterCode is null ? 0 : 2) + (ServiceCode is null ? 0 : 4);
}

/// <summary>커넥터 코드가 실제로 쓰는 읽기 전용 스냅샷 — 어떤 스코프에서 왔는지는 몰라도 된다.</summary>
public sealed class RuleConfigSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _lists;
    private readonly IReadOnlyDictionary<string, CodeMap> _maps;

    public RuleConfigSnapshot(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lists, IReadOnlyDictionary<string, CodeMap> maps)
    { _values = values; _lists = lists; _maps = maps; }

    public string Value(string key) => _values.TryGetValue(key, out var v) ? v : throw new MissingRuleException(key);
    public IReadOnlyList<string> List(string key) => _lists.TryGetValue(key, out var v) ? v : Array.Empty<string>();
    public CodeMap CodeMap(string key) => _maps.TryGetValue(key, out var v) ? v : throw new MissingRuleException(key);
}

/// <summary>코드 매핑표. 매핑 누락은 조용히 넘어가지 않고 건 단위 실패로 드러난다.</summary>
public sealed class CodeMap
{
    private readonly IReadOnlyDictionary<string, string> _map;
    public string Key { get; }
    public CodeMap(string key, IReadOnlyDictionary<string, string> map) { Key = key; _map = map; }
    public string Map(string source) => _map.TryGetValue(source, out var t) ? t : throw new MissingRuleException($"{Key}[{source}]");
}

public sealed class MissingRuleException : Exception { public MissingRuleException(string key) : base($"설정 누락: {key}") { } }

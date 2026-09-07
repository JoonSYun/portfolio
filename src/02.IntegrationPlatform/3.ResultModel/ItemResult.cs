namespace Portfolio.IntegrationPlatform.ResultModel;

/// <summary>연동 한 건이 거치는 단계. 실패는 반드시 어느 단계인지 특정된다.</summary>
public enum Stage { Fetch, Map, Validate, WmsCommit, ExternalCommit, Unknown }

public enum Verdict { Pass, Fail }

/// <summary>한 단계의 판정.</summary>
public sealed record StageVerdict(Stage Stage, Verdict Verdict, string? Detail, string? RemoteCode, DateTimeOffset At);

/// <summary>
/// [담당업무 3] 운영자 중심의 결과 모델 — 건 단위 결과에 단계별 판정을 누적한다.
///
/// 이전 알림은 "성공 97 / 실패 3" 뿐이었다. 무엇이 왜 실패했는지는 로그를 뒤져야 알 수 있었다.
/// 이 모델은 주문 한 건이 Map → Validate → WmsCommit → ExternalCommit 을 지나며 남긴 판정을
/// 순서대로 쌓는다. 그래서 실패 알림 한 줄만으로 "어떤 주문이, 어느 단계에서, 상대 코드 무엇으로"
/// 실패했는지 즉시 특정된다.
/// </summary>
public sealed class ItemResult
{
    private readonly List<StageVerdict> _verdicts = new();

    public string ItemKey { get; }
    public IReadOnlyList<StageVerdict> Verdicts => _verdicts;
    public bool IsSuccess => _verdicts.Count > 0 && _verdicts.All(v => v.Verdict == Verdict.Pass);

    /// <summary>처음 실패한 단계 — 알림과 재처리 판단의 기준점.</summary>
    public StageVerdict? FirstFailure => _verdicts.FirstOrDefault(v => v.Verdict == Verdict.Fail);

    public ItemResult(string itemKey) => ItemKey = itemKey;

    public void Pass(Stage stage, string? detail = null) =>
        _verdicts.Add(new StageVerdict(stage, Verdict.Pass, detail, null, DateTimeOffset.UtcNow));

    public void Fail(Stage stage, string detail, string? remoteCode = null) =>
        _verdicts.Add(new StageVerdict(stage, Verdict.Fail, detail, remoteCode, DateTimeOffset.UtcNow));

    /// <summary>알림 한 줄 형식: ORD-1001 ✗ ExternalCommit [E4012] 재고 없음</summary>
    public override string ToString() => FirstFailure is { } f
        ? $"{ItemKey} ✗ {f.Stage}{(f.RemoteCode is null ? "" : $" [{f.RemoteCode}]")} {f.Detail}"
        : $"{ItemKey} ✓";
}

/// <summary>연동 한 번의 집계. 알림·이력·재처리가 모두 이 객체에서 나온다.</summary>
public sealed class IntegrationRunResult
{
    public Guid CorrelationId { get; }
    public string ConnectorCode { get; }
    public string BrandCode { get; }
    public IReadOnlyList<ItemResult> Items { get; }
    public TimeSpan Elapsed { get; }

    public int TotalCount => Items.Count;
    public int SuccessCount => Items.Count(i => i.IsSuccess);
    public int FailureCount => TotalCount - SuccessCount;
    public IEnumerable<ItemResult> Failures => Items.Where(i => !i.IsSuccess);

    public IntegrationRunResult(Guid correlationId, string connector, string brand, IReadOnlyList<ItemResult> items, TimeSpan elapsed)
    { CorrelationId = correlationId; ConnectorCode = connector; BrandCode = brand; Items = items; Elapsed = elapsed; }

    /// <summary>단계별 실패 분포 — "이번 장애는 ExternalCommit 에 몰려 있다" 를 알림에서 바로 본다.</summary>
    public IReadOnlyDictionary<Stage, int> FailuresByStage() =>
        Failures.Select(f => f.FirstFailure!.Stage).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>영속화용 이력 행 (integ.Run).</summary>
public sealed class IntegrationRunRecord
{
    public Guid CorrelationId { get; set; }
    public required string ConnectorCode { get; set; }
    public required string BrandCode { get; set; }
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int ElapsedMs { get; set; }
    public required string ItemsJson { get; set; }   // ItemResult[] 전체 — 재처리 화면이 읽는다
    public DateTime CreatedAt { get; set; }
}

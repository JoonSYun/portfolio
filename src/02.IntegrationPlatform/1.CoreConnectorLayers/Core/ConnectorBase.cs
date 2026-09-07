using System.Diagnostics;
using Portfolio.IntegrationPlatform.ResultModel;
using Portfolio.IntegrationPlatform.RuleConfiguration;
using Portfolio.IntegrationPlatform.Consistency;

namespace Portfolio.IntegrationPlatform.CoreConnectorLayers.Core;

/// <summary>
/// [담당업무 1] Core / 커넥터 2계층 구조 — Core 계층.
///
/// 연동처마다 다른 것(프로토콜·업무 흐름)과 공통된 것(설정·인증·로깅·예외·전송 보장)을 분리했다.
/// 공통된 것은 전부 여기 있다. 커넥터는 <see cref="ExecuteAsync"/> 의 고유 로직만 구현한다.
/// 신규 연동처 추가는 이 클래스를 상속하는 것으로 끝나며, 기존 연동에는 손대지 않는다 (격리).
///
///   공통 관심사(Core)            고유 로직(커넥터)
///   ─ 설정 스냅샷 해석             ─ 상대 API 호출 방식
///   ─ 인증 토큰 획득/갱신           ─ 전문 매핑
///   ─ 구조적 로깅 + 상관관계 ID      ─ 업무 흐름(주문→송장→반품…)
///   ─ 예외 → 건 단위 판정 변환
///   ─ 전송 보장(Outbox) + WMS 우선 순서
/// </summary>
public abstract class ConnectorBase<TRequest, TItem> : IConnector
    where TRequest : IIntegrationRequest<TItem>
    where TItem : IIntegrationItem
{
    protected ILogger Logger { get; }
    private readonly IRuleConfigProvider _rules;
    private readonly IConnectorAuthProvider _auth;
    private readonly IOutboxWriter _outbox;

    protected ConnectorBase(ILogger logger, IRuleConfigProvider rules, IConnectorAuthProvider auth, IOutboxWriter outbox)
    { Logger = logger; _rules = rules; _auth = auth; _outbox = outbox; }

    public abstract string ConnectorCode { get; }   // 예: SABANGNET

    /// <summary>연동 한 번의 표준 흐름. 커넥터는 이 순서를 바꿀 수 없다.</summary>
    public async Task<IntegrationRunResult> RunAsync(TRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        using var scope = Logger.BeginScope(new Dictionary<string, object>
        {
            ["Connector"] = ConnectorCode, ["Brand"] = request.BrandCode, ["CorrelationId"] = correlationId
        });

        // ① 설정 스냅샷 — 브랜드·센터·서비스 단위 기본값 병합 (2.RuleConfiguration)
        var config = await _rules.SnapshotAsync(ConnectorCode, request.BrandCode, request.CenterCode, request.ServiceCode, ct);

        // ② 인증 — 토큰 캐시/갱신은 Core 가 한다
        var credential = await _auth.GetAsync(ConnectorCode, request.BrandCode, ct);

        // ③ 건 단위 실행 — 예외는 여기서 건 단위 판정으로 바뀐다 (3.ResultModel)
        var results = new List<ItemResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var itemResult = new ItemResult(item.Key);
            try
            {
                await ExecuteAsync(item, config, credential, itemResult, ct);
            }
            catch (ConnectorException ex)        // 상대 시스템이 거부 — 업무 실패
            {
                itemResult.Fail(ex.Stage, ex.Message, ex.RemoteCode);
            }
            catch (Exception ex)                 // 예상 못 한 오류 — 시스템 실패
            {
                Logger.LogError(ex, "예상치 못한 오류 {ItemKey}", item.Key);
                itemResult.Fail(Stage.Unknown, ex.Message);
            }
            results.Add(itemResult);
        }

        // ④ 전송 보장 — 결과와 후속 이벤트를 Outbox 에 함께 기록 (4.Consistency)
        var run = new IntegrationRunResult(correlationId, ConnectorCode, request.BrandCode, results, sw.Elapsed);
        await _outbox.StageAsync(run, ct);

        Logger.LogInformation("완료 {Success}/{Total} {Elapsed}ms", run.SuccessCount, run.TotalCount, sw.ElapsedMilliseconds);
        return run;
    }

    /// <summary>커넥터가 구현하는 유일한 것 — 한 건에 대한 고유 로직. 단계별 판정은 result 에 누적한다.</summary>
    protected abstract Task ExecuteAsync(TItem item, RuleConfigSnapshot config, ConnectorCredential credential, ItemResult result, CancellationToken ct);
}

public interface IConnector { string ConnectorCode { get; } }
public interface IIntegrationRequest<TItem> { string BrandCode { get; } string CenterCode { get; } string ServiceCode { get; } IReadOnlyList<TItem> Items { get; } }
public interface IIntegrationItem { string Key { get; } }

public interface IConnectorAuthProvider { Task<ConnectorCredential> GetAsync(string connector, string brand, CancellationToken ct); }
public sealed record ConnectorCredential(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>상대 시스템의 업무 거부. 어느 단계에서, 어떤 코드로 거부됐는지 들고 다닌다.</summary>
public sealed class ConnectorException : Exception
{
    public Stage Stage { get; }
    public string? RemoteCode { get; }
    public ConnectorException(Stage stage, string message, string? remoteCode = null) : base(message) { Stage = stage; RemoteCode = remoteCode; }
}

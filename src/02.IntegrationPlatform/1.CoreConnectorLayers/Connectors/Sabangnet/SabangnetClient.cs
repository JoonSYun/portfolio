using System.Net.Http.Json;
using Portfolio.IntegrationPlatform.CoreConnectorLayers.Core;
using Portfolio.IntegrationPlatform.ResultModel;

namespace Portfolio.IntegrationPlatform.CoreConnectorLayers.Connectors.Sabangnet;

/// <summary>
/// 사방넷 REST/JSON 클라이언트. HttpClientFactory + Polly(재시도/서킷) 는 DI 등록에서 건다.
/// 명세에 없는 예외 동작(HTTP 200 인데 body 의 result 가 "FAIL")까지 직접 검증해 예외로 변환한다.
/// </summary>
public sealed class SabangnetClient : ISabangnetClient
{
    private readonly HttpClient _http;
    public SabangnetClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<SabangnetOrder>> FetchNewOrdersAsync(ConnectorCredential cred, string brand, DateTime sinceUtc, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?brand={brand}&since={sinceUtc:O}");
        req.Headers.Authorization = new("Bearer", cred.AccessToken);
        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadFromJsonAsync<SabangnetEnvelope<List<SabangnetOrder>>>(cancellationToken: ct)
                   ?? throw new ConnectorException(Stage.Fetch, "빈 응답");
        EnsureOk(body, Stage.Fetch);
        return body.Data ?? new();
    }

    public async Task AcknowledgeOrderAsync(ConnectorCredential cred, string orderNo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderNo}/ack");
        req.Headers.Authorization = new("Bearer", cred.AccessToken);
        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadFromJsonAsync<SabangnetEnvelope<object>>(cancellationToken: ct)
                   ?? throw new ConnectorException(Stage.ExternalCommit, "빈 응답");
        EnsureOk(body, Stage.ExternalCommit);
    }

    /// <summary>HTTP 상태와 무관하게 body 의 result 를 본다 — 명세 밖 동작 대응.</summary>
    private static void EnsureOk<T>(SabangnetEnvelope<T> body, Stage stage)
    {
        if (!string.Equals(body.Result, "OK", StringComparison.OrdinalIgnoreCase))
            throw new ConnectorException(stage, body.Message ?? "사방넷 처리 실패", body.Code);
    }

    private sealed record SabangnetEnvelope<T>(string Result, string? Code, string? Message, T? Data);
}

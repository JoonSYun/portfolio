using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 로그 조회 도구(search_logs). 이 관리자 서버의 Seq(<see cref="DiagnosisSeqOptions"/>)에서 로그를 찾는다.
/// 실행의 대상 환경(env)과 상관없이 이 서버의 Seq 라 결과 첫 줄에 출처(배포 환경 이름으로 정함)를 적는다.
/// Seq HTTP API <c>GET api/events</c> 는 최신순으로 돌려준다 — 최근 count 건을 받아 시각 순으로 바꿔 보여 주고,
/// 한 건 더 받아 기간 안에 더 오래된 로그가 남았는지 알린다.
/// 결과 길이 상한(2만 자)에 잘리지 않게 최근 것부터 글자 예산만큼만 담는다 — 잘린다면 오래된 쪽이 빠져야
/// "이어 보려면 to=…"(보인 것 중 가장 오래된 시각)로 빠짐없이 이어 볼 수 있다.
/// </summary>
public sealed class DiagnosisSeqTools
{
    public const string HttpClientName = "diagnosis-seq";
    public const int    TimeoutSeconds = 30;

    private const string ApiKeyHeader      = "X-Seq-ApiKey";
    private const int    DefaultCount      = 50;
    private const int    MaxCount          = 200;
    private const int    MaxFilterChars    = 2_000;
    private const int    MaxMessageChars   = 1_000;
    private const int    MaxExceptionLines = 15;
    private const string KstFormat         = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>한국은 서머타임이 없어 고정 오프셋으로 바꾼다.</summary>
    private static readonly TimeSpan Kst          = TimeSpan.FromHours(9);
    private static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);
    private static readonly string[] KstInputFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
    ];

    /// <summary>서비스 이름 속성 — 서비스에 따라 Application(관리자·스케줄러) 또는 ApplicationName(연동 서비스 등).</summary>
    private static readonly string[] ServiceProps = ["Application", "ApplicationName"];

    private readonly IHttpClientFactory  _http;
    private readonly DiagnosisSeqOptions _options;
    private readonly string              _source;

    public DiagnosisSeqTools(IHttpClientFactory http, IOptions<DiagnosisOptions> options, IHostEnvironment host)
    {
        _http    = http;
        _options = options.Value.Seq;
        _source  = host.EnvironmentName switch
        {
            "Production"  => "운영 서버 Seq",
            "Staging"     => "테스트 서버(테스트 서버) Seq",
            "Development" => "개발 PC Seq",
            var name      => $"{name} 서버 Seq",
        };
    }

    /// <param name="from">시작 시각(KST). 생략하면 끝 시각 24시간 전.</param>
    /// <param name="to">끝 시각(KST). 생략하면 지금.</param>
    public async Task<string> SearchAsync(string? filter, string? from, string? to, int? count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Url))
            throw new DiagnosisToolFailureException("Diagnosis:Seq:Url 이 설정되지 않았습니다.");
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        if (filter?.Length > MaxFilterChars)
            throw new DiagnosisToolException($"filter 는 {MaxFilterChars:N0}자까지입니다.");

        var take    = Math.Clamp(count ?? DefaultCount, 1, MaxCount);
        var toUtc   = to   is null ? DatetimeHelper.UtcNow : KstToUtc(to, "to");
        var fromUtc = from is null ? toUtc - DefaultRange  : KstToUtc(from, "from");
        if (fromUtc >= toUtc)
            throw new DiagnosisToolException("from 이 to 보다 앞이어야 합니다.");

        var query = new StringBuilder($"api/events?render=true&count={take + 1}")
            .Append("&fromDateUtc=").Append(Uri.EscapeDataString(fromUtc.ToString("O")))
            .Append("&toDateUtc=").Append(Uri.EscapeDataString(toUtc.ToString("O")));
        if (filter is not null) query.Append("&filter=").Append(Uri.EscapeDataString(filter));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.Url.TrimEnd('/') + "/"), query.ToString()));
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) request.Headers.Add(ApiKeyHeader, _options.ApiKey);

        string body;
        try
        {
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            // 400 은 필터식이 틀린 것(요청 거절), 그 밖은 인증·Seq 쪽 문제(실행 실패).
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode == HttpStatusCode.BadRequest
                    ? new DiagnosisToolException(FailureMessage(response.StatusCode, body))
                    : new DiagnosisToolFailureException(FailureMessage(response.StatusCode, body));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DiagnosisToolFailureException($"Seq 조회가 {TimeoutSeconds}초 안에 끝나지 않았습니다. 기간을 줄이거나 필터를 좁혀 다시 조회하세요.");
        }
        catch (HttpRequestException ex)
        {
            throw new DiagnosisToolFailureException($"Seq 에 연결하지 못했습니다({_options.Url}): {ex.Message}");
        }

        var events = Parse(body);
        return Format(events.Take(take).ToList(), events.Count > take, filter, fromUtc, toUtc);
    }

    private static DateTime KstToUtc(string value, string arg)
    {
        if (!DateTime.TryParseExact(value.Trim(), KstInputFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var kst))
            throw new DiagnosisToolException($"{arg} 는 KST 'yyyy-MM-dd HH:mm:ss' 형식이어야 합니다: {value}");
        return DateTime.SpecifyKind(kst - Kst, DateTimeKind.Utc);
    }

    private static string FailureMessage(HttpStatusCode status, string body)
    {
        var error = SecretMasker.Mask(ErrorText(body));
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Seq 인증에 실패했습니다. Diagnosis:Seq:ApiKey 에 Read 권한 키가 설정되지 않았거나 틀립니다 — 로그 없이 진행하세요.",
            HttpStatusCode.BadRequest => $"Seq 가 요청을 거절했습니다: {error} — 필터식(Seq 문법)을 고쳐 다시 조회하세요.",
            _ => $"Seq 오류({(int)status}): {error}",
        };
    }

    /// <summary>Seq 오류 본문 <c>{"Error": "..."}</c> 의 문구. 형식이 다르면 본문 앞부분.</summary>
    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString()!;
        }
        catch (JsonException) { }
        return body.Length <= 300 ? body : body[..300] + "…";
    }

    private static List<SeqEvent> Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<SeqEvent>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            if (e.TryGetProperty("Properties", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                    if (p.TryGetProperty("Name", out var n) && p.TryGetProperty("Value", out var v))
                        props[n.GetString() ?? ""] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();

            list.Add(new SeqEvent(
                DateTimeOffset.Parse(Str(e, "Timestamp") ?? "", CultureInfo.InvariantCulture),
                Str(e, "Level") ?? "",
                Str(e, "RenderedMessage") ?? "",
                Str(e, "Exception"),
                props));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <param name="newestFirst">Seq 가 준 순서(최신순) 그대로, count 건까지.</param>
    /// <param name="more">기간 안에 count 건보다 더 있음.</param>
    private string Format(IReadOnlyList<SeqEvent> newestFirst, bool more, string? filter, DateTime fromUtc, DateTime toUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"출처: {_source}({_options.Url}) — 실행의 대상 환경과 상관없이 이 관리자 서버의 로그다.");
        sb.AppendLine($"기간(KST): {(fromUtc + Kst).ToString(KstFormat)} ~ {(toUtc + Kst).ToString(KstFormat)} · 필터: {filter ?? "(없음)"}");
        if (newestFirst.Count == 0)
        {
            sb.AppendLine("0건 — 기간·필터를 바꿔 보세요. Seq 에는 Information 이상만 남고, 보존 기간이 지난 로그는 없습니다.");
            return sb.ToString();
        }

        // 최근 것부터 글자 예산 안에서만 담는다(첫 건은 넘어도 담는다 — 결과 상한이 마지막에 자른다).
        var blocks = new List<string>();
        var used   = 0;
        foreach (var e in newestFirst)
        {
            var block = FormatEvent(e);
            if (blocks.Count > 0 && used + block.Length > DiagnosisTextFormat.MaxSliceChars) { more = true; break; }
            blocks.Add(block);
            used += block.Length;
        }

        var oldest = newestFirst[blocks.Count - 1].Timestamp.ToOffset(Kst).ToString(KstFormat);
        sb.AppendLine(more
            ? $"{blocks.Count}건(최근 것, 시각 순) — 이 기간에 더 오래된 로그가 있다. 이어 보려면 to=\"{oldest}\""
            : $"{blocks.Count}건(시각 순)");
        for (var i = blocks.Count - 1; i >= 0; i--) sb.AppendLine().Append(blocks[i]);
        return sb.ToString();
    }

    private static string FormatEvent(SeqEvent e)
    {
        var sb   = new StringBuilder();
        var head = new List<string> { $"[{e.Timestamp.ToOffset(Kst).ToString(KstFormat)}] {e.Level}" };
        var service = ServiceProps.Select(p => e.Props.GetValueOrDefault(p)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (service is not null) head.Add(service);
        if (e.Props.TryGetValue("SourceContext", out var source)) head.Add(source);
        if (e.Props.TryGetValue("LogId", out var logId)) head.Add($"LogId={logId}");
        sb.AppendLine(string.Join(" | ", head));
        // 연결 문자열이 든 예외 메시지 등 — 가린 뒤에 자른다.
        sb.AppendLine("  " + Cut(SecretMasker.Mask(e.Message), MaxMessageChars));
        if (!string.IsNullOrWhiteSpace(e.Exception))
        {
            var lines = DiagnosisTextFormat.SplitLines(SecretMasker.Mask(e.Exception.TrimEnd()));
            sb.AppendLine("  예외:");
            foreach (var line in lines.Take(MaxExceptionLines)) sb.AppendLine("    " + line);
            if (lines.Count > MaxExceptionLines) sb.AppendLine($"    … (이하 {lines.Count - MaxExceptionLines}줄 생략)");
        }
        return sb.ToString();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "… (잘림)";

    private sealed record SeqEvent(
        DateTimeOffset Timestamp, string Level, string Message, string? Exception, IReadOnlyDictionary<string, string> Props);
}

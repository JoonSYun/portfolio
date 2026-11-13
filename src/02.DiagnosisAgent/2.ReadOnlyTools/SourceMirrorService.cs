using System.Text.Json;
using Microsoft.Extensions.Options;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// GitHub 저장소를 주기적으로 진단용 복사본으로 동기화한다. 서버 시작 시 한 번, 이후 <c>SyncIntervalMinutes</c> 마다.
/// 대상은 설정의 저장소 목록이고, 목록이 비어 있으면 주기마다 소유자(acme-logistics) 저장소 전체를 다시 조회한다(새 저장소도 자동 포함).
/// 브랜치 최신 SHA 가 바뀐 저장소만 zip 을 받아 새 폴더에 풀고, 다 풀린 뒤 현재 버전을 바꿔 끼운다.
/// 실패한 저장소는 이전 버전을 그대로 쓰고 상태에 오류를 남긴다.
/// </summary>
public sealed class SourceMirrorService : BackgroundService
{
    public const string HttpClientName = "diagnosis-github";

    private readonly IOptions<DiagnosisOptions>   _options;
    private readonly SourceMirror                 _mirror;
    private readonly IHttpClientFactory           _httpFactory;
    private readonly ILogger<SourceMirrorService> _logger;

    public SourceMirrorService(
        IOptions<DiagnosisOptions>   options,
        SourceMirror                 mirror,
        IHttpClientFactory           httpFactory,
        ILogger<SourceMirrorService> logger)
    {
        _options     = options;
        _mirror      = mirror;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 기동을 붙잡지 않게 바로 백그라운드로 넘긴다(폴더 준비·기존 복사본 읽기도 기동이 끝난 뒤에 한다).
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 서버 종료
        }
        catch (Exception ex)
        {
            // 진단은 부가 기능이다. 여기서 예외가 새면 .NET 기본 설정(BackgroundServiceExceptionBehavior.StopHost)이
            // 관리자 백엔드 전체를 멈추므로(예: 운영 서버에서 SourceRoot 폴더 권한 없음) 기록만 하고 동기화를 접는다.
            _logger.LogError(ex, AppLog.Log("[Diagnosis] 소스 동기화를 멈춥니다 — 진단은 소스 없이 DB 도구로만 동작합니다."));
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opt = _options.Value;
        if (string.IsNullOrWhiteSpace(opt.SourceRoot))
        {
            _logger.LogWarning(AppLog.Log("[Diagnosis] SourceRoot 가 없어 소스 복사본을 쓰지 않습니다."));
            return;
        }

        Directory.CreateDirectory(opt.SourceRoot);
        // 토큰이 없어도 이미 받아 둔 복사본은 쓴다.
        _mirror.LoadExisting();

        if (string.IsNullOrWhiteSpace(opt.GitHub.Token))
        {
            _logger.LogWarning(AppLog.Log("[Diagnosis] GitHub.Token 이 없어 소스 동기화를 하지 않습니다."));
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var repo in await ResolveReposAsync(opt, stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                await SyncRepoAsync(opt, repo, stoppingToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, opt.SyncIntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncRepoAsync(DiagnosisOptions opt, DiagnosisRepoOptions repo, CancellationToken ct)
    {
        try
        {
            // 이전 주기에 바꿔 끼운 옛 버전은 이번 주기에 지운다. 바꾼 직후에 지우면 그 폴더를 읽던
            // 진행 중 진단(최대 실행 시간 < 동기화 주기)이 파일을 잃는다.
            if (_mirror.CurrentSha(repo.Name) is { } current)
                DeleteOldVersions(opt.SourceRoot, repo.Name, current);

            var http = _httpFactory.CreateClient(HttpClientName);
            var sha  = await GetHeadShaAsync(http, opt.GitHub.Owner, repo, ct);
            if (string.Equals(sha, _mirror.CurrentSha(repo.Name), StringComparison.OrdinalIgnoreCase))
            {
                _mirror.MarkChecked(repo.Name, DatetimeHelper.Now);
                return;
            }

            var dir = Path.Combine(opt.SourceRoot, $"{repo.Name}@{sha}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

            var tempZip = Path.Combine(Path.GetTempPath(), $"diag-{repo.Name}-{sha}.zip");
            try
            {
                using (var resp = await http.GetAsync(
                           $"repos/{opt.GitHub.Owner}/{repo.Name}/zipball/{sha}",
                           HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var file = File.Create(tempZip);
                    await resp.Content.CopyToAsync(file, ct);
                }

                int files;
                await using (var zip = File.OpenRead(tempZip))
                    files = MirrorBuilder.ExtractToMirror(zip, dir);

                await File.WriteAllTextAsync(dir + ".complete", MirrorBuilder.CompleteMarker(sha), ct);
                _mirror.SetCurrent(repo.Name, dir, sha, DatetimeHelper.Now);

                _logger.LogInformation(AppLog.Log("[Diagnosis] {Repo} 동기화 완료 sha={Sha} files={Files}"),
                    repo.Name, sha[..Math.Min(7, sha.Length)], files);
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 서버 종료 — 다음 기동 때 다시 동기화한다.
        }
        catch (Exception ex)
        {
            _mirror.SetError(repo.Name, ex.Message, DatetimeHelper.Now);
            _logger.LogWarning(ex, AppLog.Log("[Diagnosis] {Repo} 동기화 실패"), repo.Name);
        }
    }

    /// <summary>설정 목록이 있으면 그것, 없으면 소유자의 저장소 전체. 조회 실패 시 이번 주기는 건너뛴다(기존 복사본은 그대로).</summary>
    private async Task<IReadOnlyList<DiagnosisRepoOptions>> ResolveReposAsync(DiagnosisOptions opt, CancellationToken ct)
    {
        if (opt.GitHub.Repos.Count > 0) return opt.GitHub.Repos;
        try
        {
            var repos = await ListOwnerReposAsync(_httpFactory.CreateClient(HttpClientName), opt.GitHub.Owner, ct);
            if (repos.Count == 0)
                _logger.LogWarning(AppLog.Log(
                    "[Diagnosis] 토큰으로 볼 수 있는 {Owner} 저장소가 없습니다. 토큰의 Repository access 를 확인하세요."), opt.GitHub.Owner);
            else
                _logger.LogInformation(AppLog.Log("[Diagnosis] {Owner} 저장소 {Count}개를 동기화 대상으로 잡았습니다."),
                    opt.GitHub.Owner, repos.Count);
            return repos;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, AppLog.Log("[Diagnosis] {Owner} 저장소 목록 조회 실패 — 이번 주기는 건너뜁니다."), opt.GitHub.Owner);
            return [];
        }
    }

    /// <summary>
    /// 토큰이 볼 수 있는 저장소 중 소유자가 <paramref name="owner"/> 인 것을 기본 브랜치와 함께 가져온다.
    /// 소유자가 조직이든 개인 계정이든 같은 방법으로 된다(<c>/user/repos</c> 는 토큰의 접근 범위를 그대로 따른다 —
    /// acme-logistics 는 조직이 아니라 개인 계정이라 <c>/orgs/…</c> 는 404).
    /// 보관(archived)·사용 중지·빈 저장소는 뺀다(빈 저장소는 zip 을 받을 수 없다).
    /// </summary>
    private static async Task<List<DiagnosisRepoOptions>> ListOwnerReposAsync(HttpClient http, string owner, CancellationToken ct)
    {
        const int pageSize = 100;
        var repos = new List<DiagnosisRepoOptions>();
        for (var page = 1; ; page++)
        {
            using var resp = await http.GetAsync(
                $"user/repos?affiliation=owner,collaborator,organization_member&per_page={pageSize}&page={page}", ct);
            resp.EnsureSuccessStatusCode();
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);

            var count = 0;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                count++;
                var repoOwner = r.GetProperty("owner").GetProperty("login").GetString();
                if (!string.Equals(repoOwner, owner, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.GetProperty("archived").GetBoolean() || r.GetProperty("disabled").GetBoolean()) continue;
                if (r.GetProperty("size").GetInt64() == 0) continue;
                repos.Add(new DiagnosisRepoOptions
                {
                    Name   = r.GetProperty("name").GetString()!,
                    Branch = r.GetProperty("default_branch").GetString() ?? "main",
                });
            }
            if (count < pageSize) break;
        }
        return repos;
    }

    /// <summary><c>application/vnd.github.sha</c> 로 요청하면 본문이 SHA 문자열 하나다.</summary>
    private static async Task<string> GetHeadShaAsync(HttpClient http, string owner, DiagnosisRepoOptions repo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{owner}/{repo.Name}/commits/{Uri.EscapeDataString(repo.Branch)}");
        req.Headers.Accept.ParseAdd("application/vnd.github.sha");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsStringAsync(ct)).Trim();
    }

    private static void DeleteOldVersions(string root, string repo, string keepSha)
    {
        foreach (var d in Directory.GetDirectories(root, $"{repo}@*"))
        {
            if (d.EndsWith("@" + keepSha, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(d, recursive: true);
                File.Delete(d + ".complete");
            }
            catch (IOException)
            {
                // 진행 중인 진단이 읽고 있으면 다음 동기화 때 지운다.
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 진단용 소스 복사본(UTF-8)의 현재 버전을 들고, 파일 찾기·검색·읽기를 한다.
/// <para>
/// 저장소별 폴더는 <c>{Root}/{저장소}@{SHA}</c> 이고, 다 만든 폴더 옆에 <c>{저장소}@{SHA}.complete</c> 표시 파일을 둔다.
/// 동기화(<see cref="SourceMirrorService"/>)가 새 버전을 다 만든 뒤 <see cref="SetCurrent"/> 로 바꿔 끼우므로
/// 검색은 늘 완성된 폴더만 본다.
/// </para>
/// </summary>
public sealed class SourceMirror
{
    public const int MaxFindResults   = 200;
    public const int MaxSearchResults = 100;
    private const int MaxLineChars    = 300;

    private readonly ConcurrentDictionary<string, RepoEntry> _repos = new(StringComparer.OrdinalIgnoreCase);

    public SourceMirror(string root) => Root = root ?? "";

    public string Root { get; }

    // ── 버전 관리 ─────────────────────────────────────────────

    /// <summary>
    /// 서버 재시작 시 이미 만들어 둔 최신 복사본을 다시 잡는다. 저장소 목록을 몰라도 되게 완료 표시 파일
    /// <c>{저장소}@{SHA}.complete</c> 를 모두 훑어 저장소마다 가장 최근 것을 고른다(GitHub 저장소 이름에는 '@' 가 없다).
    /// 옛 가리기 규칙(또는 가리기 전)으로 만든 복사본은 잡지 않는다 — 다음 동기화가 같은 SHA 를 다시 받는다
    /// (<see cref="MirrorBuilder.IsCurrentMarker"/>).
    /// </summary>
    public void LoadExisting()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root)) return;
        var latest = new DirectoryInfo(Root).GetFiles("*@*.complete")
            .Where(f => MirrorBuilder.IsCurrentMarker(File.ReadAllText(f.FullName)))
            .GroupBy(f => RepoNameOf(Path.GetFileNameWithoutExtension(f.Name)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.LastWriteTimeUtc).First());
        foreach (var marker in latest)
        {
            var dirName = Path.GetFileNameWithoutExtension(marker.Name);
            var dir     = Path.Combine(Root, dirName);
            if (!Directory.Exists(dir)) continue;
            var at = dirName.LastIndexOf('@');
            SetCurrent(dirName[..at], dir, dirName[(at + 1)..], marker.LastWriteTime);
        }

        static string RepoNameOf(string dirName) => dirName[..dirName.LastIndexOf('@')];
    }

    public void SetCurrent(string repo, string directory, string sha, DateTime syncedAt)
    {
        var e = Entry(repo);
        lock (e) { e.Directory = directory; e.Sha = sha; e.SyncedAt = syncedAt; e.CheckedAt = syncedAt; e.Error = null; }
    }

    public void MarkChecked(string repo, DateTime at)
    {
        var e = Entry(repo);
        lock (e) { e.CheckedAt = at; e.Error = null; }
    }

    public void SetError(string repo, string error, DateTime at)
    {
        var e = Entry(repo);
        lock (e) { e.CheckedAt = at; e.Error = error; }
    }

    public string? CurrentSha(string repo)
    {
        if (!_repos.TryGetValue(repo, out var e)) return null;
        lock (e) return e.Sha;
    }

    /// <summary>진단 기록에 남길 저장소별 짧은 SHA(7자리).</summary>
    public IReadOnlyDictionary<string, string> CurrentShas()
        => GetStates().Where(s => s.Sha is not null)
                      .ToDictionary(s => s.Name, s => s.Sha![..Math.Min(7, s.Sha!.Length)]);

    public IReadOnlyList<SourceRepoStateDto> GetStates()
        => _repos.Values
            .Select(e =>
            {
                lock (e)
                    return new SourceRepoStateDto
                    {
                        Name = e.Name, Sha = e.Sha, SyncedAt = e.SyncedAt, CheckedAt = e.CheckedAt, Error = e.Error,
                    };
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── 도구 ──────────────────────────────────────────────────

    /// <summary>파일 이름(또는 '/' 가 들어간 경로) 패턴으로 파일을 찾는다. 와일드카드 * ?, ** 는 폴더 여러 단계.</summary>
    public string FindFiles(string namePattern, string? repo)
    {
        var regex = GlobToRegex(namePattern.Contains('/') ? namePattern : "**/" + namePattern);
        var hits  = new List<string>();
        var total = 0;
        foreach (var (name, dir) in Targets(repo))
            foreach (var rel in EnumerateRelative(dir))
            {
                if (!regex.IsMatch(rel)) continue;
                total++;
                if (hits.Count < MaxFindResults) hits.Add($"{name}/{rel}");
            }

        if (total == 0) return $"'{namePattern}' 와 맞는 파일이 없습니다.";
        var sb = new StringBuilder();
        sb.AppendLine(total > hits.Count ? $"{total}개 (앞 {hits.Count}개만 표시 — repo 나 패턴으로 좁히세요)" : $"{total}개");
        foreach (var h in hits) sb.AppendLine(h);
        return sb.ToString();
    }

    /// <summary>정규식으로 줄 단위 검색. 결과는 "저장소/경로:줄: 내용".</summary>
    public string Search(string pattern, string? repo, string? pathGlob, bool ignoreCase)
    {
        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new DiagnosisToolException($"정규식 오류: {ex.Message}");
        }

        var pathRegex = string.IsNullOrWhiteSpace(pathGlob)
            ? null
            : GlobToRegex(pathGlob.Contains('/') ? pathGlob : "**/" + pathGlob);
        var hits       = new List<string>();
        var total      = 0;
        var skipped    = 0;
        var unreadable = 0;
        foreach (var (name, dir) in Targets(repo))
            foreach (var rel in EnumerateRelative(dir))
            {
                if (pathRegex is not null && !pathRegex.IsMatch(rel)) continue;
                try
                {
                    var lineNo = 0;
                    foreach (var line in File.ReadLines(Path.Combine(dir, rel)))
                    {
                        lineNo++;
                        if (!regex.IsMatch(line)) continue;
                        total++;
                        if (hits.Count < MaxSearchResults) hits.Add($"{name}/{rel}:{lineNo}: {Clip(line.Trim())}");
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    skipped++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable++;   // 동기화가 옛 버전을 지우는 중 등 — 그 파일만 건너뛴다
                }
            }

        var sb = new StringBuilder();
        if (total == 0) sb.AppendLine($"'{pattern}' 검색 결과가 없습니다.");
        else sb.AppendLine(total > hits.Count ? $"{total}건 (앞 {hits.Count}건만 표시 — repo·path_glob 으로 좁히세요)" : $"{total}건");
        foreach (var h in hits) sb.AppendLine(h);
        if (skipped > 0) sb.AppendLine($"(정규식 시간 초과로 {skipped}개 파일을 건너뜀 — 패턴을 단순하게 바꾸세요)");
        if (unreadable > 0) sb.AppendLine($"(읽을 수 없는 파일 {unreadable}개를 건너뜀)");
        return sb.ToString();
    }

    /// <summary>파일의 줄 범위를 읽는다. 경로 앞에 저장소 이름이 붙어 있어도 받아 준다. 저장소 밖 경로는 거절.</summary>
    public string ReadFile(string repo, string path, int startLine, int? endLine)
    {
        var dir      = RepoDirectory(repo);
        var relative = path.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith(repo + "/", StringComparison.OrdinalIgnoreCase))
            relative = relative[(repo.Length + 1)..];

        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new DiagnosisToolException("저장소 밖의 경로는 읽을 수 없습니다.");
        if (!File.Exists(full))
            throw new DiagnosisToolException($"파일이 없습니다: {repo}/{relative} (find_files 로 경로를 확인하세요)");

        return DiagnosisTextFormat.NumberedSlice($"{repo}/{relative}", File.ReadAllLines(full), startLine, endLine);
    }

    // ── 내부 ──────────────────────────────────────────────────

    private RepoEntry Entry(string repo) => _repos.GetOrAdd(repo, n => new RepoEntry { Name = n });

    private string RepoDirectory(string repo)
    {
        if (_repos.TryGetValue(repo ?? "", out var e))
            lock (e)
                if (e.Directory is not null) return e.Directory;

        var available = string.Join(", ", GetStates().Where(s => s.Sha is not null).Select(s => s.Name));
        throw new DiagnosisToolException(
            $"'{repo}' 저장소의 소스 복사본이 없습니다. 있는 저장소: {(available.Length > 0 ? available : "없음(동기화 전)")}");
    }

    private IEnumerable<(string Name, string Dir)> Targets(string? repo)
    {
        if (!string.IsNullOrWhiteSpace(repo)) return [(repo, RepoDirectory(repo))];

        var all = _repos.Values
            .Select(e => { lock (e) return (e.Name, e.Directory); })
            .Where(t => t.Directory is not null)
            .Select(t => (t.Name, t.Directory!))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (all.Count == 0)
            throw new DiagnosisToolFailureException(
                "소스 복사본이 아직 없습니다(동기화 전). DB 도구로 진행하고, 코드를 못 본 점을 unverified 에 적으세요.");
        return all;
    }

    private static IEnumerable<string> EnumerateRelative(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                    .Order(StringComparer.Ordinal);

    private static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Clip(string s) => s.Length <= MaxLineChars ? s : s[..MaxLineChars] + "…";

    private sealed class RepoEntry
    {
        public required string Name { get; init; }
        public string?   Directory  { get; set; }
        public string?   Sha        { get; set; }
        public DateTime? SyncedAt   { get; set; }
        public DateTime? CheckedAt  { get; set; }
        public string?   Error      { get; set; }
    }
}

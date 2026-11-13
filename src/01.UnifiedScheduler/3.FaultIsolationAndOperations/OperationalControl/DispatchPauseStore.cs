using System.Text.Json;
using Microsoft.Extensions.Options;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Dispatch 전역 일시정지 상태의 영속 / 메모리 캐시 싱글톤.
/// 파일(<see cref="DispatchPauseOptions.FilePath"/>)을 single source of truth 로 삼되,
/// <see cref="Portfolio.UnifiedScheduler.Jobs.Dispatcher.Fire"/> 가 분 단위로 들이받는 hot path 비용을
/// 0 으로 낮추기 위해 생성자에서 파일을 1회 로드하고 메모리에 들고 있는다.
/// 토글(<see cref="Set"/>)만 파일 I/O 가 발생하며, atomic write 로 process crash 시 손상을 방지한다.
/// </summary>
public sealed class DispatchPauseStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string                       _absolutePath;
    private readonly ILogger<DispatchPauseStore>  _logger;
    private readonly object                       _lock = new();

    /// <summary>메모리 캐시. <see cref="Current"/> getter 와 <see cref="Set"/> 의 갱신만 접근하며 lock 으로 보호한다.</summary>
    private DispatchPauseState _state;

    /// <summary>
    /// 시작 시점에 파일을 1회 로드해 메모리에 적재한다. 파일이 없으면 기본 상태(unpaused)로 시작하고
    /// 다음 <see cref="Set"/> 호출 시점에 파일이 자동 생성된다(쓰기 위치 미존재로 인한 갑작스러운 실패 회피).
    /// </summary>
    /// <param name="opt">파일 경로 설정. <see cref="DispatchPauseOptions.FilePath"/> 는 ContentRoot 기준 상대경로 허용.</param>
    /// <param name="env">상대경로 해소 기준점(ContentRoot = .Web 프로젝트 폴더).</param>
    /// <param name="logger">파일 부재 / 손상 / IO 실패 시 진단 로그 출력 경로.</param>
    public DispatchPauseStore(
        IOptions<DispatchPauseOptions> opt,
        IHostEnvironment               env,
        ILogger<DispatchPauseStore>    logger)
    {
        _logger = logger;

        var configured = opt.Value.FilePath;
        _absolutePath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));

        _state = LoadOrDefault();
        _logger.LogInformation(AppLog.Log(
            "DispatchPauseStore initialized. Path={Path} IsPaused={IsPaused}"),
            _absolutePath, _state.IsPaused);
    }

    /// <summary>
    /// 현재 일시정지 상태의 스냅샷. 호출자에게 mutable 참조를 노출하지 않기 위해 새 인스턴스로 복제해 반환한다.
    /// Dispatcher.Fire 가드는 본 메서드를 호출해 <see cref="DispatchPauseState.IsPaused"/> 만 검사한다.
    /// </summary>
    public DispatchPauseState Current
    {
        get
        {
            lock (_lock)
            {
                return new DispatchPauseState
                {
                    IsPaused = _state.IsPaused,
                    PausedDt = _state.PausedDt,
                    PausedBy = _state.PausedBy,
                    Reason   = _state.Reason
                };
            }
        }
    }

    /// <summary>
    /// 상태를 토글하고 파일에 즉시 영속한다. 호출 후 다음 fire 부터 가드가 반영된다.
    /// <paramref name="isPaused"/> 가 false 면 <see cref="DispatchPauseState.Reason"/> /
    /// <see cref="DispatchPauseState.PausedBy"/> 등은 마지막 pause 시점의 값을 그대로 둔다(이력 보존).
    /// </summary>
    /// <param name="isPaused">true = cron 자동 발사 차단, false = 정상 운영.</param>
    /// <param name="user">토글 수행자 UserName(Email). null/공백이면 "UNKNOWN" 으로 기록.</param>
    /// <param name="reason">토글 사유(자유 텍스트, 선택).</param>
    public void Set(bool isPaused, string? user, string? reason)
    {
        var next = new DispatchPauseState
        {
            IsPaused = isPaused,
            PausedDt = isPaused ? DatetimeHelper.Now : _state.PausedDt,
            PausedBy = isPaused ? (string.IsNullOrWhiteSpace(user) ? "UNKNOWN" : user) : _state.PausedBy,
            Reason   = isPaused ? reason : _state.Reason
        };

        lock (_lock)
        {
            PersistAtomic(next);
            _state = next;
        }

        _logger.LogInformation(AppLog.Log(
            "DispatchPauseStore toggled. IsPaused={IsPaused} By={By} Reason={Reason}"),
            next.IsPaused, next.PausedBy, next.Reason);
    }

    /// <summary>
    /// 파일을 JSON 으로 역직렬화해 적재. 파일 부재 / 파싱 실패 시 unpaused 기본값으로 fallback 하고 경고 로그만 남긴다 —
    /// 서비스 자체는 불안정한 상태 파일 때문에 부팅이 막히면 안 되므로 fail-open(=정상 운영) 정책.
    /// </summary>
    private DispatchPauseState LoadOrDefault()
    {
        if (!File.Exists(_absolutePath))
        {
            _logger.LogWarning(AppLog.Log(
                "DispatchPause file missing — defaulting to unpaused. Path={Path}"),
                _absolutePath);
            return new DispatchPauseState();
        }

        try
        {
            var json = File.ReadAllText(_absolutePath);
            var loaded = JsonSerializer.Deserialize<DispatchPauseState>(json, JsonOpts);
            return loaded ?? new DispatchPauseState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, AppLog.Log(
                "DispatchPause file load failed — defaulting to unpaused. Path={Path}"),
                _absolutePath);
            return new DispatchPauseState();
        }
    }

    /// <summary>
    /// temp 파일에 직렬화 후 <see cref="File.Move(string, string, bool)"/> 로 원자적 교체. 도중 crash 발생 시
    /// 기존 파일은 손상되지 않는다(temp 만 고아로 남거나 새 파일로 swap 둘 중 하나).
    /// 디렉토리 부재 시 자동 생성 — 최초 토글 시점에 파일이 없는 경로여도 안전하게 안착.
    /// </summary>
    private void PersistAtomic(DispatchPauseState state)
    {
        var dir = Path.GetDirectoryName(_absolutePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = _absolutePath + ".tmp";
        var json     = JsonSerializer.Serialize(state, JsonOpts);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _absolutePath, overwrite: true);
    }
}

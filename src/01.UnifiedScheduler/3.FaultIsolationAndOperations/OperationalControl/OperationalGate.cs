using System.Collections.Concurrent;

namespace Portfolio.UnifiedScheduler.FaultIsolationAndOperations.OperationalControl;

/// <summary>운영자가 제어를 걸 수 있는 수준. 어느 수준이든 즉시 적용된다.</summary>
public enum ControlLevel { Domain, Group, Brand, Queue }

public enum GateDecision
{
    Allow,
    Paused,          // 1단: 전역 일시정지 (외부 점검에 따른 전면 중단)
    Blocked,         // 2단: 도메인·그룹·브랜드·큐 차단 (특정 업무/브랜드 부분 배제)
    ValidationOnly   // 3단: 검증 모드 (신규 도메인 시범 가동 — 부작용 없이 경로만 태움)
}

public interface IOperationalGate
{
    GateDecision Evaluate(string domain, string group, string brand, string queue);

    void PauseAll(string operatorId, string reason);
    void ResumeAll(string operatorId);
    void Block(ControlLevel level, string key, string operatorId, string reason);
    void Unblock(ControlLevel level, string key, string operatorId);
    void SetValidation(ControlLevel level, string key, bool on, string operatorId);
    OperationalSnapshot Snapshot();
}

/// <summary>
/// [담당업무 3] 3단 운영 제어 — 현장에서 확인한 "스케줄러를 멈춰야 하는 세 가지 상황"에 1:1 대응.
///
///   ① 외부 점검으로 전면 중단      → PauseAll
///   ② 특정 브랜드·업무 부분 배제   → Block(Domain|Group|Brand|Queue)
///   ③ 신규 도메인 시범 가동        → SetValidation(…)
///
/// 판정은 Filter 단계에서 매 발사마다 이뤄지고, 변경은 API 호출 즉시 반영된다.
/// 개발자 개입도, 배포도 없다. 모든 변경은 누가·언제·왜 걸었는지 감사 로그로 남는다.
/// </summary>
public sealed class OperationalGate : IOperationalGate
{
    private readonly ConcurrentDictionary<string, ControlEntry> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ControlEntry> _validation = new(StringComparer.OrdinalIgnoreCase);
    private readonly IControlAuditLog _audit;
    private volatile ControlEntry? _pause;

    public OperationalGate(IControlAuditLog audit) => _audit = audit;

    public GateDecision Evaluate(string domain, string group, string brand, string queue)
    {
        if (_pause is not null) return GateDecision.Paused;

        // 어느 수준에서든 하나라도 걸려 있으면 차단 — 넓은 범위(큐/그룹)와 좁은 범위(브랜드) 모두 유효
        if (Hit(_blocked, domain, group, brand, queue)) return GateDecision.Blocked;
        if (Hit(_validation, domain, group, brand, queue)) return GateDecision.ValidationOnly;
        return GateDecision.Allow;
    }

    public void PauseAll(string operatorId, string reason)
    {
        _pause = new ControlEntry(operatorId, reason, DateTimeOffset.UtcNow);
        _audit.Write("PAUSE_ALL", "*", operatorId, reason);
    }

    public void ResumeAll(string operatorId) { _pause = null; _audit.Write("RESUME_ALL", "*", operatorId, null); }

    public void Block(ControlLevel level, string key, string operatorId, string reason)
    {
        _blocked[Key(level, key)] = new ControlEntry(operatorId, reason, DateTimeOffset.UtcNow);
        _audit.Write("BLOCK", Key(level, key), operatorId, reason);
    }

    public void Unblock(ControlLevel level, string key, string operatorId)
    {
        _blocked.TryRemove(Key(level, key), out _);
        _audit.Write("UNBLOCK", Key(level, key), operatorId, null);
    }

    public void SetValidation(ControlLevel level, string key, bool on, string operatorId)
    {
        if (on) _validation[Key(level, key)] = new ControlEntry(operatorId, "validation", DateTimeOffset.UtcNow);
        else _validation.TryRemove(Key(level, key), out _);
        _audit.Write(on ? "VALIDATION_ON" : "VALIDATION_OFF", Key(level, key), operatorId, null);
    }

    public OperationalSnapshot Snapshot() => new(_pause, _blocked.ToDictionary(), _validation.ToDictionary());

    private static string Key(ControlLevel level, string key) => $"{level}:{key}";

    private static bool Hit(ConcurrentDictionary<string, ControlEntry> set, string domain, string group, string brand, string queue) =>
        set.ContainsKey(Key(ControlLevel.Queue, queue)) ||
        set.ContainsKey(Key(ControlLevel.Group, group)) ||
        set.ContainsKey(Key(ControlLevel.Domain, domain)) ||
        set.ContainsKey(Key(ControlLevel.Brand, brand)) ||
        set.ContainsKey(Key(ControlLevel.Brand, $"{domain}/{brand}"));   // 특정 도메인의 특정 브랜드만
}

public sealed record ControlEntry(string OperatorId, string? Reason, DateTimeOffset At);
public sealed record OperationalSnapshot(ControlEntry? GlobalPause,
    IReadOnlyDictionary<string, ControlEntry> Blocked, IReadOnlyDictionary<string, ControlEntry> Validation);

public interface IControlAuditLog { void Write(string action, string target, string operatorId, string? reason); }

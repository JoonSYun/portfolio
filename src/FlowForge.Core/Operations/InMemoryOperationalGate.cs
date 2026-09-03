using System.Collections.Concurrent;
using FlowForge.Abstractions.Operations;

namespace FlowForge.Core.Operations;

/// <summary>
/// Thread-safe operational gate. A block registered at the domain level
/// (<c>jobKey</c>) covers all tenants; a block registered at <c>jobKey::tenant</c>
/// covers one tenant. Global pause and validation mode are single flags.
/// </summary>
public sealed class InMemoryOperationalGate : IOperationalGate
{
    private readonly ConcurrentDictionary<string, byte> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _paused;
    private volatile bool _validationMode;

    private static string Key(string jobKey, string? tenantId) =>
        tenantId is null ? jobKey : $"{jobKey}::{tenantId}";

    public GateDecision Evaluate(string jobKey, string tenantId)
    {
        if (_paused)
            return GateDecision.Paused;
        if (_blocked.ContainsKey(jobKey) || _blocked.ContainsKey(Key(jobKey, tenantId)))
            return GateDecision.Blocked;
        if (_validationMode)
            return GateDecision.ValidationOnly;
        return GateDecision.Allow;
    }

    public void PauseGlobally() => _paused = true;
    public void ResumeGlobally() => _paused = false;
    public void Block(string jobKey, string? tenantId = null) => _blocked[Key(jobKey, tenantId)] = 0;
    public void Unblock(string jobKey, string? tenantId = null) => _blocked.TryRemove(Key(jobKey, tenantId), out _);
    public void SetValidationMode(bool enabled) => _validationMode = enabled;

    public OperationalStatus Snapshot() => new()
    {
        GloballyPaused = _paused,
        ValidationMode = _validationMode,
        BlockedKeys = _blocked.Keys.OrderBy(k => k).ToList()
    };
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Portfolio.UnifiedScheduler.FaultIsolationAndOperations.OperationalControl;

/// <summary>
/// [담당업무 3] 운영자가 콘솔에서 직접 누르는 3단 제어 API. JWT 의 Operator 역할만 허용.
/// </summary>
[ApiController]
[Route("api/control")]
[Authorize(Roles = "Operator")]
public sealed class OperationalControlController : ControllerBase
{
    private readonly IOperationalGate _gate;
    public OperationalControlController(IOperationalGate gate) => _gate = gate;

    private string Operator => User.Identity?.Name ?? "unknown";

    [HttpGet] public OperationalSnapshot Get() => _gate.Snapshot();

    // 1단 — 전역
    [HttpPost("pause")]  public IActionResult Pause([FromBody] ReasonDto dto) { _gate.PauseAll(Operator, dto.Reason); return Ok(_gate.Snapshot()); }
    [HttpPost("resume")] public IActionResult Resume() { _gate.ResumeAll(Operator); return Ok(_gate.Snapshot()); }

    // 2단 — 도메인/그룹/브랜드/큐
    [HttpPost("block/{level}/{key}")]
    public IActionResult Block(ControlLevel level, string key, [FromBody] ReasonDto dto)
    { _gate.Block(level, key, Operator, dto.Reason); return Ok(_gate.Snapshot()); }

    [HttpDelete("block/{level}/{key}")]
    public IActionResult Unblock(ControlLevel level, string key)
    { _gate.Unblock(level, key, Operator); return Ok(_gate.Snapshot()); }

    // 3단 — 검증 모드
    [HttpPut("validation/{level}/{key}")]
    public IActionResult Validation(ControlLevel level, string key, [FromQuery] bool on)
    { _gate.SetValidation(level, key, on, Operator); return Ok(_gate.Snapshot()); }
}

public sealed record ReasonDto(string Reason);

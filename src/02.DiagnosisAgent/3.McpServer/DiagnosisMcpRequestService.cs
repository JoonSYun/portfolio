using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Portfolio.Admin.Infrastructure.Auth;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>
/// MCP 요청·호출 기록(docs/DIAGNOSIS_MCP_REQUEST_DESIGN_2026-11.md).
/// 서버가 대화 단위를 알아볼 값이 없어(stateless) 요청 ID 를 발급하고, 조회 도구는 이 ID 가 있어야 돈다.
/// 다른 사용자의 ID 는 없는 ID 와 같게 다룬다. 사용자 질문·답변 내용은 받지도 저장하지도 않는다.
/// </summary>
public sealed class DiagnosisMcpRequestService
{
    private const string IdPrefix      = "req_";
    private const string IdChars       = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int    IdRandomChars = 8;

    private readonly AdminIdentityDbContext _db;

    public DiagnosisMcpRequestService(AdminIdentityDbContext db) => _db = db;

    public static string NewId() => IdPrefix + RandomNumberGenerator.GetString(IdChars, IdRandomChars);

    /// <summary>요청을 연다(행 추가).</summary>
    public async Task StartAsync(DiagnosisMcpRequestEntity request, CancellationToken ct)
    {
        _db.DiagnosisMcpRequests.Add(request);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>이 사용자의 요청 ID 인지. 없는 ID·남의 ID 는 false.</summary>
    public Task<bool> IsOwnedAsync(string id, string userId, CancellationToken ct)
        => _db.DiagnosisMcpRequests.AsNoTracking().AnyAsync(r => r.Id == id && r.UserId == userId, ct);

    /// <summary>
    /// 호출 1건을 남긴다. 같은 요청 범위에서 앞선 저장이 실패해 추적 중인 행이 남아 있을 수 있어 비우고 넣는다.
    /// 호출이 취소돼도 기록은 남겨야 하므로 취소 토큰을 받지 않는다.
    /// </summary>
    public async Task RecordCallAsync(DiagnosisMcpCallEntity call)
    {
        _db.ChangeTracker.Clear();
        _db.DiagnosisMcpCalls.Add(call);
        await _db.SaveChangesAsync(CancellationToken.None);
    }
}

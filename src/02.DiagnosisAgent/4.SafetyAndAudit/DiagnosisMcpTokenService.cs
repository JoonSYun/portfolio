using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portfolio.Admin.Infrastructure.Auth;
using Portfolio.Admin.Models;
using Portfolio.Admin.Models.Auth;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent.Mcp;

/// <summary>
/// 진단 MCP 개인 토큰 발급·폐기·목록과 인증 대조(docs/DIAGNOSIS_MCP_DESIGN_2026-10.md 5장).
/// 1인 1개는 PK(UserId)가 강제하고, 원문은 발급 응답에만 싣는다 — 서버에는 SHA-256 해시만 남는다.
/// </summary>
public sealed class DiagnosisMcpTokenService
{
    public const string TokenPrefix = "wsd_";

    /// <summary>목록에 보일 앞부분 길이 — "wsd_" + 6글자.</summary>
    private const int ShownChars = 10;

    private readonly AdminIdentityDbContext _db;
    private readonly DiagnosisMcpOptions    _options;

    public DiagnosisMcpTokenService(AdminIdentityDbContext db, IOptions<DiagnosisOptions> options)
    {
        _db      = db;
        _options = options.Value.Mcp;
    }

    public async Task<DiagnosisMcpTokenStatusDto> GetMineAsync(string userId, CancellationToken ct)
        => ToStatus(await _db.DiagnosisMcpTokens.AsNoTracking().SingleOrDefaultAsync(t => t.UserId == userId, ct));

    /// <summary>발급과 재발급은 같은 동작이다 — 행을 새 해시로 덮어써 이전 토큰은 그 순간 못 쓴다.</summary>
    public async Task<DiagnosisMcpTokenIssuedDto> IssueAsync(string userId, CancellationToken ct)
    {
        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now   = DatetimeHelper.Now;
        var row   = await _db.DiagnosisMcpTokens.SingleOrDefaultAsync(t => t.UserId == userId, ct);
        if (row is null)
        {
            row = new DiagnosisMcpTokenEntity { UserId = userId };
            _db.DiagnosisMcpTokens.Add(row);
        }
        row.TokenHash   = Hash(token);
        row.TokenPrefix = token[..ShownChars];
        row.CreatedAt   = now;
        row.ExpiresAt   = now.AddDays(_options.TokenLifetimeDays);
        row.LastUsedAt  = null;
        row.LastUsedIp  = null;
        await _db.SaveChangesAsync(ct);
        return new DiagnosisMcpTokenIssuedDto(token, ToStatus(row));
    }

    /// <summary>폐기 = 행 삭제. 본인 폐기와 관리자 폐기가 같다.</summary>
    public async Task RevokeAsync(string userId, CancellationToken ct)
    {
        var deleted = await _db.DiagnosisMcpTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw new NotFoundException("폐기할 MCP 토큰이 없습니다. 이미 폐기되었을 수 있습니다.");
    }

    /// <summary>MCP 토큰 관리 화면 목록 — 만료된 행도 그대로 보인다(재발급하면 덮어쓴다).</summary>
    public async Task<IReadOnlyList<DiagnosisMcpTokenRowDto>> ListAsync(CancellationToken ct)
    {
        var now = DatetimeHelper.Now;
        return await (
            from t in _db.DiagnosisMcpTokens.AsNoTracking()
            join u in _db.Users.AsNoTracking() on t.UserId equals u.Id
            orderby u.UserName
            select new DiagnosisMcpTokenRowDto(
                t.UserId, u.UserName ?? "", u.Name, t.TokenPrefix,
                t.CreatedAt, t.ExpiresAt, t.ExpiresAt <= now, t.LastUsedAt, t.LastUsedIp))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 인증 대조 — 해시가 맞고 만료 전이며 주인 계정이 승인·잠금 해제 상태면 (계정, 현재 역할)을 돌려주고 마지막 사용을 남긴다.
    /// 역할을 매번 DB 에서 읽으므로 계정을 막거나 역할을 바꾸면 토큰을 폐기하지 않아도 다음 요청부터 반영된다.
    /// </summary>
    public async Task<(AdminUser User, IReadOnlyList<string> Roles)?> AuthenticateAsync(string token, string? ip, CancellationToken ct)
    {
        var hash = Hash(token);
        var hit = await (
            from t in _db.DiagnosisMcpTokens.AsNoTracking()
            join u in _db.Users.AsNoTracking() on t.UserId equals u.Id
            where t.TokenHash == hash
            select new { t.ExpiresAt, User = u })
            .SingleOrDefaultAsync(ct);
        if (hit is null || hit.ExpiresAt <= DatetimeHelper.Now) return null;

        var user = hit.User;
        if (user.RegisterStatus != RegisterStatus.APPROVED.ToString()) return null;
        // Identity 의 잠금 판정과 같다(UserManager.IsLockedOutAsync). LockoutEnd 는 UTC 다.
        if (user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow) return null;

        var roles = await (
            from ur in _db.UserRoles.AsNoTracking()
            join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where ur.UserId == user.Id
            select r.Name!)
            .ToListAsync(ct);

        await _db.DiagnosisMcpTokens
            .Where(t => t.TokenHash == hash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LastUsedAt, (DateTime?)DatetimeHelper.Now)
                .SetProperty(t => t.LastUsedIp, ip), ct);
        return (user, roles);
    }

    private static DiagnosisMcpTokenStatusDto ToStatus(DiagnosisMcpTokenEntity? row) => row is null
        ? new(DiagnosisMcpTokenStates.None, null, null, null, null, null)
        : new(row.ExpiresAt <= DatetimeHelper.Now ? DiagnosisMcpTokenStates.Expired : DiagnosisMcpTokenStates.Active,
              row.TokenPrefix, row.CreatedAt, row.ExpiresAt, row.LastUsedAt, row.LastUsedIp);

    /// <summary>SHA-256 대문자 hex 64자.</summary>
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

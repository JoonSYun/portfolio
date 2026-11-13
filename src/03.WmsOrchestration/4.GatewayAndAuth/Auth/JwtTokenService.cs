using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Portfolio.WmsOrchestration.Model;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Portfolio.WmsOrchestration.Auth.Service
{
    /// <summary>
    /// JWT Token issuing service
    /// 
    /// - Auth 서버 전용
    /// - SignalR UserId 매핑 공식 규칙 준수 (ClaimTypes.NameIdentifier)
    /// - ASP.NET Core [Authorize], [Authorize(Roles=...)] 호환
    /// - RFC 6749 준수
    /// </summary>
    public class JwtTokenService
    {
        private readonly JwtOptions _options;

        public JwtTokenService(IOptions<JwtOptions> options)
        {
            _options = options.Value;
        }

        /// <summary>
        /// JWT Access Token 생성
        /// </summary>
        public string CreateToken(
            IdentityUser user,
            IList<Claim> authorizationClaims)
        {
            ArgumentNullException.ThrowIfNull(user);
            if (string.IsNullOrWhiteSpace(_options.SecretKey))
                throw new InvalidOperationException("Jwt SecretKey is missing.");

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_options.SecretKey));
            var credentials = new SigningCredentials(
                key, SecurityAlgorithms.HmacSha256);

            // =====================================================
            // Claims (SignalR + ASP.NET Core + RFC 6749)
            // =====================================================
            var claims = new List<Claim>
            {
                // SignalR UserIdentifier & JWT sub claim (공식 기준)
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                
                // 사용자 이름 (HttpContext.User.Identity.Name)
                new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
                
                // JWT 고유 ID
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };

            // Role & Permission claims (Authorization 지원)
            if (authorizationClaims != null && authorizationClaims.Count > 0)
            {
                claims.AddRange(authorizationClaims);
            }

            // =====================================================
            // Token 생성
            // =====================================================
            var token = new JwtSecurityToken(
                issuer: _options.Issuer,
                audience: _options.Audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}

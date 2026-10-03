using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Services;

public class JwtOptions
{
    public string Issuer { get; set; } = "wfm";
    public string Audience { get; set; } = "wfm-clients";
    public string Key { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public class TokenService(JwtOptions options, WfmDbContext db, UserManager<AppUser> users)
{
    public async Task<AuthResponse> IssueAsync(AppUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email!),
            new(WfmClaims.FullName, user.FullName),
            new(WfmClaims.TenantId, user.TenantId.ToString()),
            new(WfmClaims.TenantName, tenant.Name),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var expires = DateTime.UtcNow.AddMinutes(options.AccessTokenMinutes);
        var jwt = new JwtSecurityToken(options.Issuer, options.Audience, claims, expires: expires,
            signingCredentials: new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256));

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refresh),
            ExpiresAt = DateTime.UtcNow.AddDays(options.RefreshTokenDays)
        });
        await db.SaveChangesAsync();

        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(jwt), refresh, expires,
            Mapping.ToDto(user, roles, tenant.Name));
    }

    /// <summary>Refresh token'ı doğrular, eskisini iptal eder (rotation) ve yeni token çifti üretir.</summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash);
        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt < DateTime.UtcNow) return null;

        var user = await users.FindByIdAsync(stored.UserId.ToString());
        if (user is null || !user.IsActive) return null;

        stored.RevokedAt = DateTime.UtcNow;
        return await IssueAsync(user);
    }

    public async Task RevokeAsync(string refreshToken)
    {
        var hash = Hash(refreshToken);
        await db.RefreshTokens.Where(r => r.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTime.UtcNow));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

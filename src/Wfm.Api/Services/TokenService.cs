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

public class TokenService(JwtOptions options, WfmDbContext db, UserManager<AppUser> users, VehicleObserverSessionService observerSessions)
{
    public async Task<AuthResponse> IssueAsync(AppUser user, Guid? observerSessionId = null)
    {
        var roles = await users.GetRolesAsync(user);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId);
        VehicleObserverSession? observerSession = null;
        if (roles.Contains(Roles.VehicleObserver))
        {
            if (observerSessionId is { } sid)
                observerSession = await observerSessions.FindActiveAsync(sid, user.TenantId, user.Id)
                    ?? throw new InvalidOperationException("Denetçi oturumu sonlandı.");
            else
            {
                var duration = await db.VehicleObserverAccesses.IgnoreQueryFilters()
                    .Where(a => a.TenantId == user.TenantId && a.UserId == user.Id)
                    .Select(a => (int?)a.SessionDurationMinutes).SingleOrDefaultAsync() ?? 30;
                observerSession = new VehicleObserverSession { TenantId = user.TenantId, UserId = user.Id,
                    FullName = user.FullName, ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Clamp(duration, 1, 1440)) };
                db.VehicleObserverSessions.Add(observerSession);
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email!),
            new(WfmClaims.FullName, user.FullName),
            new(WfmClaims.TenantId, user.TenantId.ToString()),
            new(WfmClaims.TenantName, tenant.Name),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (observerSession is not null) claims.Add(new Claim(VehicleObserverSessionService.ClaimName, observerSession.Id.ToString()));

        var expires = DateTime.UtcNow.AddMinutes(options.AccessTokenMinutes);
        if (observerSession is not null && observerSession.ExpiresAt < expires) expires = observerSession.ExpiresAt;
        var jwt = new JwtSecurityToken(options.Issuer, options.Audience, claims, expires: expires,
            signingCredentials: new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256));

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            VehicleObserverSessionId = observerSession?.Id,
            TokenHash = Hash(refresh),
            ExpiresAt = observerSession?.ExpiresAt ?? DateTime.UtcNow.AddDays(options.RefreshTokenDays)
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
        if (!await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive)) return null;
        if (await users.IsInRoleAsync(user, Roles.VehicleObserver) &&
            (stored.VehicleObserverSessionId is not { } sid || await observerSessions.FindActiveAsync(sid, user.TenantId, user.Id) is null)) return null;

        stored.RevokedAt = DateTime.UtcNow;
        return await IssueAsync(user, stored.VehicleObserverSessionId);
    }

    public async Task RevokeAsync(string refreshToken, bool connectionClosed = false)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(r => r.TokenHash == hash);
        if (stored is null) return;
        stored.RevokedAt ??= DateTime.UtcNow;
        if (stored.VehicleObserverSessionId is { } id)
            await observerSessions.CloseAsync(id, stored.UserId, connectionClosed ? "ConnectionClosed" : "Logout");
        await db.SaveChangesAsync();
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

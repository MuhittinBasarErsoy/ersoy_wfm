using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain;

namespace Wfm.Shared.UI.Services;

/// <summary>Blazor kimlik durumunu <see cref="AuthSession"/>'dan üretir.</summary>
public sealed class WfmAuthStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly AuthSession _session;

    public WfmAuthStateProvider(AuthSession session)
    {
        _session = session;
        _session.Changed += OnChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync() => Build(await _session.GetAsync());

    private void OnChanged(AuthResponse? auth) => NotifyAuthenticationStateChanged(Task.FromResult(Build(auth)));

    private static AuthenticationState Build(AuthResponse? auth)
    {
        if (auth is null) return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        var u = auth.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.FullName),
            new(ClaimTypes.Email, u.Email),
            new(WfmClaims.TenantId, u.TenantId.ToString()),
            new(WfmClaims.TenantName, u.TenantName),
        };
        claims.AddRange(u.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "wfm", ClaimTypes.Name, ClaimTypes.Role)));
    }

    public void Dispose() => _session.Changed -= OnChanged;
}

/// <summary>Aktif kullanıcı ve yetki kontrolleri. API ile aynı politika tablosunu (<see cref="Policies.Map"/>) kullanır.</summary>
public class UserContext(AuthSession session)
{
    public UserDto? User => session.Current?.User;
    public bool IsAuthenticated => User is not null;
    public bool Can(string policy) => User is not null && Policies.Map[policy].Any(r => User.Roles.Contains(r));
    public bool IsFieldWorker => User?.Roles.Contains(Roles.FieldWorker) == true;
    public bool IsSuperAdmin => User?.Roles.Contains(Roles.SuperAdmin) == true;
    public string RoleName => User?.Roles.FirstOrDefault() is { } r ? Roles.DisplayName(r) : "";
}

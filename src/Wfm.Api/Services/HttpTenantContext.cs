using System.Security.Claims;
using Wfm.Application;
using Wfm.Domain;

namespace Wfm.Api.Services;

public class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public Guid? TenantId => Guid.TryParse(User?.FindFirstValue(WfmClaims.TenantId), out var id) ? id : null;
    public Guid? UserId => Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public bool IsSuperAdmin => User?.IsInRole(Roles.SuperAdmin) ?? false;
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static Guid TenantId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(WfmClaims.TenantId)!);

    public static bool HasPolicy(this ClaimsPrincipal user, string policy) =>
        Policies.Map[policy].Any(user.IsInRole);
}

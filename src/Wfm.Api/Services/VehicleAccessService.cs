using Microsoft.EntityFrameworkCore;
using Wfm.Application;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Services;

public class VehicleAccessService(WfmDbContext db)
{
    public IQueryable<LocationPing> LatestLocations(IQueryable<AppUser> visible, Guid tid, DateTime cutoff, DateTime now) =>
        (from p in db.LocationPings.AsNoTracking()
         join u in visible on p.UserId equals u.Id
         where p.TenantId == tid && p.IsVehicleLocation && p.VehiclePlate == u.VehiclePlate &&
             p.RecordedAt >= cutoff && p.RecordedAt >= u.VehicleTrackingEnabledAt && p.RecordedAt <= now.AddMinutes(5)
         select p).GroupBy(p => p.UserId)
            .Select(group => group.OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.Id).First());

    public async Task<IQueryable<AppUser>?> VisibleUsersAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var tid = ctx.User.TenantId();
        var uid = ctx.User.UserId();
        var query = db.Users.AsNoTracking().Where(u => u.TenantId == tid && u.IsActive && u.VehicleTrackingEnabled);
        if (!ctx.User.IsInRole(Roles.VehicleObserver)) return query;
        var access = await db.VehicleObserverAccesses.AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantId == tid && a.UserId == uid, ct);
        if (access is null || !access.IsEnabled || access.ExpiresAt <= DateTime.UtcNow) return null;
        if (!VehicleAccessScheduleRules.Allows(access.WeeklyScheduleJson, DateTimeOffset.UtcNow)) return null;
        if (!access.AllVehicles)
        {
            var ids = db.VehicleObserverVehicles.Where(v => v.TenantId == tid && v.ObserverId == uid).Select(v => v.VehicleUserId);
            query = query.Where(u => ids.Contains(u.Id));
        }
        return query;
    }
}

public static class VehicleObserverBoundary
{
    // A dedicated role is never allowed to fall through to old endpoints that only require a login.
    public static async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole(Roles.VehicleObserver) &&
            !Allows(ctx.Request.Method, ctx.Request.Path))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new Wfm.Application.Contracts.ApiError("Bu hesap yalnızca araç takip ekranına erişebilir."));
            return;
        }
        await next(ctx);
    }

    public static bool Allows(string method, PathString path)
    {
        if (HttpMethods.IsGet(method) && (path == "/api/auth/me" || path.StartsWithSegments("/api/vehicles"))) return true;
        return HttpMethods.IsPost(method) && (path == "/api/auth/login" || path == "/api/auth/refresh" || path == "/api/auth/logout");
    }
}

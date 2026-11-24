using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

public static class VehicleEndpoints
{
    public static void MapVehicleEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/vehicles").WithTags("Vehicles").RequireAuthorization(Policies.ViewVehicles);
        g.MapGet("/", async (HttpContext ctx, VehicleAccessService access, WfmDbContext db, CancellationToken ct) =>
        {
            var visible = await access.VisibleUsersAsync(ctx, ct);
            if (visible is null) return Results.Json(new ApiError("Araç takip erişiminiz kapalı veya süresi dolmuş."), statusCode: 403);
            var tid = ctx.User.TenantId();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tid, ct);
            var users = await visible.OrderBy(u => u.VehiclePlate).ToListAsync(ct);
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-tenant.LocationRetentionDays);
            var pings = await access.LatestLocations(visible, tid, cutoff, now).ToListAsync(ct);
            var latest = pings.ToDictionary(p => p.UserId);
            var ids = users.Select(u => u.Id).ToList();
            var onShift = await db.Shifts.AsNoTracking().Where(s => s.TenantId == tid && s.EndedAt == null && ids.Contains(s.UserId))
                .Select(s => s.UserId).Distinct().ToListAsync(ct);
            var rows = users.Select(u =>
            {
                latest.TryGetValue(u.Id, out var p);
                return new VehicleLocationDto(u.Id, u.VehiclePlate ?? "", u.VehicleLabel, u.FullName, onShift.Contains(u.Id),
                    p?.Latitude, p?.Longitude, VehicleTrackingRules.SpeedKmh(p?.Speed), p?.Heading, p?.Accuracy, p?.BatteryLevel, p?.RecordedAt);
            }).ToList();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new VehicleSnapshotDto(tenant.DefaultLatitude, tenant.DefaultLongitude, now, tenant.LocationRetentionDays, rows));
        });

        g.MapGet("/{userId:guid}/history", async (Guid userId, DateTimeOffset from, DateTimeOffset to,
            HttpContext ctx, VehicleAccessService access, WfmDbContext db, CancellationToken ct) =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(7))
                return Results.BadRequest(new ApiError("Rota aralığı en fazla 7 gün olmalı."));
            var visible = await access.VisibleUsersAsync(ctx, ct);
            if (visible is null) return Results.Json(new ApiError("Araç takip erişiminiz kapalı veya süresi dolmuş."), statusCode: 403);
            var user = await visible.SingleOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();
            var tid = ctx.User.TenantId();
            var f = from.UtcDateTime; var t = to.UtcDateTime;
            var retentionDays = await db.Tenants.Where(x => x.Id == tid).Select(x => x.LocationRetentionDays).SingleAsync(ct);
            var earliest = DateTime.UtcNow.AddDays(-retentionDays);
            if (f < earliest) f = earliest;
            var raw = await db.LocationPings.AsNoTracking().Where(p => p.TenantId == tid && p.UserId == userId && p.IsVehicleLocation &&
                    p.VehiclePlate == user.VehiclePlate && p.RecordedAt >= user.VehicleTrackingEnabledAt && p.RecordedAt >= f && p.RecordedAt < t)
                .OrderBy(p => p.RecordedAt).ThenBy(p => p.Id).Take(20001)
                .Select(p => new { p.Latitude, p.Longitude, p.RecordedAt, p.Speed, p.Accuracy }).ToListAsync(ct);
            var points = raw.Take(20000).Select(p => new VehicleTrackPoint(p.Latitude, p.Longitude, p.RecordedAt,
                VehicleTrackingRules.SpeedKmh(p.Speed), p.Accuracy));
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(VehicleTrackingRules.History(points, raw.Count > 20000));
        });

        // Inspector administration is deliberately outside the observer's read-only API group.
        var admin = app.MapGroup("/api/vehicle-access").WithTags("Vehicle access").RequireAuthorization(Policies.ManageUsers);
        admin.MapGet("/", async (HttpContext ctx, WfmDbContext db, CancellationToken ct) =>
        {
            var tid = ctx.User.TenantId();
            var observers = await (from u in db.Users.AsNoTracking()
                                   join ur in db.UserRoles on u.Id equals ur.UserId
                                   join role in db.Roles on ur.RoleId equals role.Id
                                   where u.TenantId == tid && role.Name == Roles.VehicleObserver
                                   select new { u.Id, u.FullName }).ToListAsync(ct);
            var grants = await db.VehicleObserverAccesses.AsNoTracking().Where(a => a.TenantId == tid).ToListAsync(ct);
            var vehicles = await db.VehicleObserverVehicles.AsNoTracking().Where(a => a.TenantId == tid).ToListAsync(ct);
            return observers.Select(u =>
            {
                var a = grants.SingleOrDefault(g => g.UserId == u.Id);
                return new VehicleAccessDto(u.Id, u.FullName, a?.IsEnabled ?? false, a?.AllVehicles ?? false, a?.ExpiresAt,
                    vehicles.Where(v => v.ObserverId == u.Id).Select(v => v.VehicleUserId).ToList());
            }).ToList();
        });
        admin.MapPut("/{userId:guid}", async (Guid userId, SaveVehicleAccessRequest req, HttpContext ctx, WfmDbContext db, CancellationToken ct) =>
        {
            var tid = ctx.User.TenantId();
            var isObserver = await (from u in db.Users
                                    join ur in db.UserRoles on u.Id equals ur.UserId
                                    join role in db.Roles on ur.RoleId equals role.Id
                                    where u.Id == userId && u.TenantId == tid && role.Name == Roles.VehicleObserver select u.Id).AnyAsync(ct);
            if (!isObserver) return Results.NotFound();
            if (req.VehicleUserIds is null || req.VehicleUserIds.Count > 1000 || req.IsEnabled && req.ExpiresAt <= DateTimeOffset.UtcNow)
                return Results.BadRequest(new ApiError("Geçerli bir erişim süresi ve araç listesi seçin."));
            var ids = req.AllVehicles ? [] : req.VehicleUserIds.Distinct().ToList();
            if (ids.Count != await db.Users.CountAsync(u => u.TenantId == tid && u.VehicleTrackingEnabled && u.IsActive && ids.Contains(u.Id), ct))
                return Results.BadRequest(new ApiError("Araç listesinde bu şirkete ait olmayan veya pasif bir kayıt var."));
            var a = await db.VehicleObserverAccesses.SingleOrDefaultAsync(x => x.TenantId == tid && x.UserId == userId, ct);
            if (a is null) { a = new VehicleObserverAccess { TenantId = tid, UserId = userId }; db.VehicleObserverAccesses.Add(a); }
            a.IsEnabled = req.IsEnabled; a.AllVehicles = req.AllVehicles; a.ExpiresAt = req.ExpiresAt?.UtcDateTime;
            var old = await db.VehicleObserverVehicles.Where(v => v.TenantId == tid && v.ObserverId == userId).ToListAsync(ct);
            db.VehicleObserverVehicles.RemoveRange(old.Where(v => !ids.Contains(v.VehicleUserId)));
            db.VehicleObserverVehicles.AddRange(ids.Except(old.Select(v => v.VehicleUserId))
                .Select(id => new VehicleObserverVehicle { TenantId = tid, ObserverId = userId, VehicleUserId = id }));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}

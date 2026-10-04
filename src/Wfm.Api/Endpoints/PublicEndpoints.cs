using Microsoft.EntityFrameworkCore;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

/// <summary>Oturum gerektirmeyen uç noktalar: müşterinin görevini izlediği takip sayfası.</summary>
public static class PublicEndpoints
{
    public const string LogoPathPrefix = "/api/public/tenant-logo/";

    public static void MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        // Giriş ekranı markası: ?sirket=kisa-ad ile ya da cihazda hatırlanan şirket için.
        app.MapGet("/api/public/brand/{slug}", async (string slug, WfmDbContext db) =>
        {
            var t = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Slug == slug.ToLower() && x.IsActive && x.Slug != "system");
            return t is null ? Results.NotFound() : Results.Ok(new PublicTenantBrandDto(t.Name, t.Slug, t.BrandColor, t.LogoUrl));
        }).AllowAnonymous().WithTags("Public");

        app.MapGet(LogoPathPrefix + "{id:guid}", async (Guid id, WfmDbContext db, IFileStorage storage, HttpContext ctx) =>
        {
            var t = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
            if (t?.LogoPath is null) return Results.NotFound();
            var stream = await storage.OpenReadAsync(t.LogoPath);
            if (stream is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(stream, t.LogoContentType ?? "image/png");
        }).AllowAnonymous().WithTags("Public");

        app.MapGet("/api/public/track/{token}", async (string token, WfmDbContext db) =>
        {
            if (token.Length is < 16 or > 64) return Results.NotFound();
            var task = await db.Tasks.IgnoreQueryFilters().Include(t => t.Events)
                .FirstOrDefaultAsync(t => t.TrackingToken == token);
            if (task is null) return Results.NotFound();
            // Kapanan görevin linki bir gün sonra geçersiz olur.
            if (task.CompletedAt is { } done && done < DateTime.UtcNow.AddDays(-1)) return Results.NotFound();
            if (task.Status == WorkTaskStatus.Cancelled && task.UpdatedAt < DateTime.UtcNow.AddDays(-1)) return Results.NotFound();

            var tenant = await db.Tenants.FirstAsync(t => t.Id == task.TenantId);
            string? worker = null;
            double? wLat = null, wLng = null;
            DateTime? wAt = null;
            if (task.AssigneeId is { } a)
            {
                var name = await db.Users.Where(u => u.Id == a).Select(u => u.FullName).FirstOrDefaultAsync();
                worker = name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                // Çalışanın konumu yalnızca müşteriye doğru yoldayken paylaşılır.
                if (task.Status == WorkTaskStatus.EnRoute)
                {
                    var since = DateTime.UtcNow.AddMinutes(-30);
                    var ping = await db.LocationPings.IgnoreQueryFilters()
                        .Where(p => p.UserId == a && p.RecordedAt >= since)
                        .OrderByDescending(p => p.RecordedAt).FirstOrDefaultAsync();
                    if (ping is not null) (wLat, wLng, wAt) = (ping.Latitude, ping.Longitude, ping.RecordedAt);
                }
            }

            var events = task.Events.OrderBy(e => e.CreatedAt)
                .Where(e => e.ToStatus is WorkTaskStatus.Assigned or WorkTaskStatus.Accepted or WorkTaskStatus.EnRoute
                    or WorkTaskStatus.OnSite or WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
                .Select(e => new PublicTrackingEventDto(e.ToStatus, e.CreatedAt)).ToList();

            return Results.Ok(new PublicTrackingDto(tenant.Name, tenant.BrandColor, tenant.LogoUrl,
                task.Title, task.Status, task.Address, task.Latitude, task.Longitude,
                task.ScheduledStart, task.ScheduledEnd, task.CompletedAt, worker, wLat, wLng, wAt, events));
        }).AllowAnonymous().WithTags("Public");
    }
}

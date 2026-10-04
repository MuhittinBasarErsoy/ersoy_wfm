using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

/// <summary>Ekler, konum takibi, mesai, bildirimler ve offline senkronizasyon.</summary>
public static class FieldEndpoints
{
    private const long MaxUploadBytes = 10 * 1024 * 1024;
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/png", "image/webp", "image/heic", "application/pdf"];

    public static void MapFieldEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- Ekler ----------
        app.MapPost("/api/tasks/{id:guid}/attachments", async (Guid id, [FromForm] IFormFile file, [FromForm] AttachmentKind kind,
            [FromForm] double? latitude, [FromForm] double? longitude, [FromForm] DateTime? capturedAt, [FromForm] Guid? clientId,
            HttpContext ctx, WfmDbContext db, IFileStorage storage, FileUrlSigner signer) =>
        {
            var user = ctx.User;
            var task = await TaskEndpoints.Scope(db.Tasks, user).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();
            if (!user.HasPolicy(Policies.ManageTasks) && task.AssigneeId != user.UserId())
                return Results.Json(new ApiError("Bu göreve dosya ekleyemezsiniz."), statusCode: 403);
            if (file.Length is 0 or > MaxUploadBytes) return Results.BadRequest(new ApiError("Dosya boş veya 10 MB'tan büyük."));
            if (!AllowedTypes.Contains(file.ContentType)) return Results.BadRequest(new ApiError("Desteklenmeyen dosya türü."));

            if (clientId is { } cid && await db.Attachments.FirstOrDefaultAsync(a => a.ClientId == cid && a.WorkTaskId == id) is { } existing)
                return Results.Ok(ToDto(existing, signer)); // offline tekrar gönderimi

            var ext = file.ContentType switch { "image/png" => ".png", "image/webp" => ".webp", "image/heic" => ".heic", "application/pdf" => ".pdf", _ => ".jpg" };
            var att = new TaskAttachment
            {
                TenantId = task.TenantId, WorkTaskId = id, UploadedById = user.UserId(), Kind = kind,
                FileName = Path.GetFileName(file.FileName), ContentType = file.ContentType, SizeBytes = file.Length,
                Latitude = latitude, Longitude = longitude, CapturedAt = capturedAt?.ToUniversalTime() ?? DateTime.UtcNow,
                ClientId = clientId
            };
            await using (var s = file.OpenReadStream())
                att.StoragePath = await storage.SaveAsync(s, $"{task.TenantId:N}/{id:N}/{att.Id:N}{ext}");
            db.Attachments.Add(att);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(att, signer));
        }).RequireAuthorization().DisableAntiforgery();

        app.MapDelete("/api/tasks/{id:guid}/attachments/{attId:guid}", async (Guid id, Guid attId, HttpContext ctx, WfmDbContext db, IFileStorage storage) =>
        {
            var att = await db.Attachments.FirstOrDefaultAsync(a => a.Id == attId && a.WorkTaskId == id);
            if (att is null) return Results.NotFound();
            var task = await db.Tasks.FirstAsync(t => t.Id == id);
            if (!task.IsOpen) return Results.BadRequest(new ApiError("Kapanmış görevin eki silinemez."));
            if (!ctx.User.HasPolicy(Policies.ManageTasks) && att.UploadedById != ctx.User.UserId())
                return Results.Json(new ApiError("Bu eki silemezsiniz."), statusCode: 403);
            db.Attachments.Remove(att);
            await db.SaveChangesAsync();
            await storage.DeleteAsync(att.StoragePath);
            return Results.NoContent();
        }).RequireAuthorization();

        // İmzalı URL ile dosya indirme (kimlik doğrulama başlığı gerekmez).
        app.MapGet("/files/{id:guid}", async (Guid id, long exp, string sig, FileUrlSigner signer, WfmDbContext db, IFileStorage storage) =>
        {
            if (!signer.Validate(id, exp, sig)) return Results.Unauthorized();
            var att = await db.Attachments.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == id);
            if (att is null) return Results.NotFound();
            var stream = await storage.OpenReadAsync(att.StoragePath);
            return stream is null ? Results.NotFound() : Results.File(stream, att.ContentType, enableRangeProcessing: true);
        }).AllowAnonymous();

        // ---------- Konum takibi ----------
        var tracking = app.MapGroup("/api/tracking").WithTags("Tracking").RequireAuthorization();

        tracking.MapPost("/pings", async (List<LocationPingDto> pings, HttpContext ctx, TrackingService svc) =>
        {
            if (pings.Count > 1000) return Results.BadRequest(new ApiError("Tek seferde en fazla 1000 konum gönderilebilir."));
            await svc.RecordAsync(ctx.User.TenantId(), ctx.User.UserId(), pings);
            return Results.NoContent();
        }).RequireAuthorization(Policies.FieldWork);

        tracking.MapGet("/live", async (HttpContext ctx, WfmDbContext db) =>
        {
            var tid = ctx.User.TenantId();
            var since = DateTime.UtcNow.AddHours(-24);
            var latest = await db.LocationPings.Where(p => p.RecordedAt >= since)
                .GroupBy(p => p.UserId)
                .Select(g => g.OrderByDescending(p => p.RecordedAt).First())
                .ToListAsync();
            var userIds = latest.Select(p => p.UserId).ToList();
            var names = await db.Users.Where(u => u.TenantId == tid && userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
            var onShift = await db.Shifts.Where(s => s.EndedAt == null).Select(s => s.UserId).ToListAsync();
            var active = await db.Tasks.Where(t => t.AssigneeId != null && userIds.Contains(t.AssigneeId.Value) &&
                    (t.Status == WorkTaskStatus.EnRoute || t.Status == WorkTaskStatus.OnSite || t.Status == WorkTaskStatus.Accepted))
                .Select(t => new { t.AssigneeId, t.Id, t.Title, t.Status }).ToListAsync();

            return latest.Where(p => names.ContainsKey(p.UserId)).Select(p =>
            {
                var task = active.Where(a => a.AssigneeId == p.UserId).OrderByDescending(a => a.Status).FirstOrDefault();
                return new WorkerLocationDto(p.UserId, names[p.UserId], p.Latitude, p.Longitude, p.RecordedAt, p.BatteryLevel,
                    p.Speed, onShift.Contains(p.UserId), task?.Id, task?.Title);
            }).ToList();
        }).RequireAuthorization(Policies.ViewTracking);

        tracking.MapGet("/history/{userId:guid}", async (Guid userId, DateTime? from, DateTime? to, WfmDbContext db) =>
        {
            var f = from?.ToUniversalTime() ?? DateTime.UtcNow.Date;
            var t = to?.ToUniversalTime() ?? DateTime.UtcNow.AddMinutes(1);
            return await db.LocationPings.Where(p => p.UserId == userId && p.RecordedAt >= f && p.RecordedAt < t)
                .OrderBy(p => p.RecordedAt).Take(5000)
                .Select(p => new LocationPingDto(p.Latitude, p.Longitude, p.Accuracy, p.Speed, p.Heading, p.BatteryLevel, p.RecordedAt))
                .ToListAsync();
        }).RequireAuthorization(Policies.ViewTracking);

        // ---------- Mesai ----------
        tracking.MapGet("/shift", async (HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            var s = await db.Shifts.Where(x => x.UserId == uid && x.EndedAt == null).FirstOrDefaultAsync();
            return s is null ? Results.NoContent() : Results.Ok(new ShiftDto(s.Id, s.StartedAt, s.EndedAt));
        });

        tracking.MapPost("/shift/start", async (HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            var s = await db.Shifts.Where(x => x.UserId == uid && x.EndedAt == null).FirstOrDefaultAsync();
            if (s is null)
            {
                s = new WorkShift { UserId = uid };
                db.Shifts.Add(s);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new ShiftDto(s.Id, s.StartedAt, s.EndedAt));
        }).RequireAuthorization(Policies.FieldWork);

        tracking.MapPost("/shift/end", async (HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            var s = await db.Shifts.Where(x => x.UserId == uid && x.EndedAt == null).FirstOrDefaultAsync();
            if (s is null) return Results.NoContent();
            s.EndedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new ShiftDto(s.Id, s.StartedAt, s.EndedAt));
        }).RequireAuthorization(Policies.FieldWork);

        // ---------- Bildirimler ----------
        var notif = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();
        notif.MapGet("/", async (HttpContext ctx, WfmDbContext db, bool? unreadOnly) =>
        {
            var uid = ctx.User.UserId();
            return await db.Notifications.Where(n => n.UserId == uid && (unreadOnly != true || n.ReadAt == null))
                .OrderByDescending(n => n.CreatedAt).Take(100)
                .Select(n => new NotificationDto(n.Id, n.Title, n.Body, n.WorkTaskId, n.CreatedAt, n.ReadAt, n.Kind)).ToListAsync();
        });
        notif.MapPost("/{id:guid}/read", async (Guid id, HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            await db.Notifications.Where(n => n.Id == id && n.UserId == uid && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
            return Results.NoContent();
        });
        notif.MapPost("/read-all", async (HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            await db.Notifications.Where(n => n.UserId == uid && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
            return Results.NoContent();
        });

        // ---------- Offline senkronizasyon (mobil) ----------
        app.MapGet("/api/sync", async (HttpContext ctx, WfmDbContext db, UserDirectory dir, FileUrlSigner signer) =>
        {
            var uid = ctx.User.UserId();
            var tid = ctx.User.TenantId();
            var since = DateTime.UtcNow.AddDays(-2);
            // Açık görevlerin tamamı + son 2 günde kapananlar; istemci yerel önbelleği bununla değiştirir.
            var tasks = await db.Tasks.Where(t => t.AssigneeId == uid &&
                    (t.Status == WorkTaskStatus.Assigned || t.Status == WorkTaskStatus.Accepted || t.Status == WorkTaskStatus.EnRoute ||
                     t.Status == WorkTaskStatus.OnSite || t.UpdatedAt >= since))
                .Include(t => t.TaskType).Include(t => t.Events).Include(t => t.Attachments)
                .AsSplitQuery().ToListAsync();
            var names = await dir.NamesAsync(tid);
            var types = await db.TaskTypes.ToListAsync();
            var notifications = await db.Notifications.Where(n => n.UserId == uid && n.CreatedAt >= since)
                .OrderByDescending(n => n.CreatedAt).Take(50)
                .Select(n => new NotificationDto(n.Id, n.Title, n.Body, n.WorkTaskId, n.CreatedAt, n.ReadAt, n.Kind)).ToListAsync();
            var shift = await db.Shifts.Where(s => s.UserId == uid && s.EndedAt == null)
                .Select(s => new ShiftDto(s.Id, s.StartedAt, s.EndedAt)).FirstOrDefaultAsync();

            return new SyncResponse(DateTime.UtcNow,
                tasks.OrderByDescending(t => t.Priority).ThenBy(t => t.ScheduledStart ?? t.CreatedAt)
                    .Select(t => t.ToDetailDto(names, signer)).ToList(),
                types.Select(t => t.ToDto()).ToList(), notifications, shift);
        }).RequireAuthorization();
    }

    private static AttachmentDto ToDto(TaskAttachment a, FileUrlSigner signer) =>
        new(a.Id, a.Kind, a.FileName, a.ContentType, signer.CreateUrl(a.Id), a.Latitude, a.Longitude, a.CapturedAt, a.UploadedById);
}

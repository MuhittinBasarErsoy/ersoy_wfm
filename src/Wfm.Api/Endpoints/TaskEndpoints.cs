using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").WithTags("Tasks").RequireAuthorization();

        g.MapGet("/", async ([AsParameters] TaskQueryParams q, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var query = Scope(db.Tasks.Include(t => t.TaskType), ctx.User);
            if (q.Statuses is { Length: > 0 }) query = query.Where(t => q.Statuses.Contains(t.Status));
            if (q.OnlyOpen) query = query.Where(t => t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled && t.Status != WorkTaskStatus.Failed);
            if (q.AssigneeId is { } a) query = query.Where(t => t.AssigneeId == a);
            if (q.TaskTypeId is { } tt) query = query.Where(t => t.TaskTypeId == tt);
            if (q.From is { } from) query = query.Where(t => (t.ScheduledStart ?? t.CreatedAt) >= from);
            if (q.To is { } to) query = query.Where(t => (t.ScheduledStart ?? t.CreatedAt) < to);
            if (!string.IsNullOrWhiteSpace(q.Search))
                query = query.Where(t => t.Title.Contains(q.Search) || t.Address.Contains(q.Search) || (t.CustomerName != null && t.CustomerName.Contains(q.Search)));

            var total = await query.CountAsync();
            var page = Math.Max(1, q.Page ?? 1);
            var size = Math.Clamp(q.PageSize ?? 50, 1, 500);
            var items = await query.OrderByDescending(t => t.Priority).ThenBy(t => t.ScheduledStart ?? t.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            var names = await dir.NamesAsync(ctx.User.TenantId());
            return new PagedResult<WorkTaskDto>(items.Select(t => t.ToDto(names)).ToList(), total);
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, WfmDbContext db, UserDirectory dir, FileUrlSigner signer) =>
        {
            var task = await LoadDetail(Scope(db.Tasks, ctx.User), id);
            if (task is null) return Results.NotFound();
            return Results.Ok(task.ToDetailDto(await dir.NamesAsync(ctx.User.TenantId()), signer));
        });

        g.MapPost("/", async (SaveWorkTaskRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir,
            NotificationService notify) =>
        {
            var type = await db.TaskTypes.FirstOrDefaultAsync(t => t.Id == req.TaskTypeId);
            if (type is null) return Results.BadRequest(new ApiError("Görev tipi bulunamadı."));
            var errors = Validate(req, type);
            if (await InvalidAssignee(db, ctx.User, req.AssigneeId)) errors.Add("Atanan kullanıcı bu şirkette bir saha çalışanı değil.");
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));

            var userId = ctx.User.UserId();
            var task = new WorkTask { TenantId = ctx.User.TenantId(), CreatedById = userId };
            Apply(task, req);
            if (req.AssigneeId is not null) task.Assign(req.AssigneeId, userId);
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            task.TaskType = type;

            var dto = task.ToDto(await dir.NamesAsync(task.TenantId));
            if (task.AssigneeId is { } assignee)
                await notify.NotifyAsync(task.TenantId, assignee, "Yeni görev", $"{type.Name}: {task.Title}", task.Id);
            await notify.TaskChangedAsync(task.TenantId, dto);
            return Results.Ok(dto);
        }).RequireAuthorization(Policies.ManageTasks);

        g.MapPut("/{id:guid}", async (Guid id, SaveWorkTaskRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir,
            NotificationService notify) =>
        {
            var task = await db.Tasks.Include(t => t.TaskType).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();
            if (!task.IsOpen) return Results.BadRequest(new ApiError("Kapanmış görev düzenlenemez."));
            var type = task.TaskTypeId == req.TaskTypeId ? task.TaskType! : await db.TaskTypes.FirstOrDefaultAsync(t => t.Id == req.TaskTypeId);
            if (type is null) return Results.BadRequest(new ApiError("Görev tipi bulunamadı."));
            var errors = Validate(req, type);
            if (await InvalidAssignee(db, ctx.User, req.AssigneeId)) errors.Add("Atanan kullanıcı bu şirkette bir saha çalışanı değil.");
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));

            if (!string.IsNullOrEmpty(req.RowVersion))
                db.Entry(task).Property(t => t.RowVersion).OriginalValue = Convert.FromBase64String(req.RowVersion);

            var previousAssignee = task.AssigneeId;
            Apply(task, req);
            task.TaskType = type;
            if (req.AssigneeId != previousAssignee) task.Assign(req.AssigneeId, ctx.User.UserId());
            await db.SaveChangesAsync();

            var dto = task.ToDto(await dir.NamesAsync(task.TenantId));
            if (req.AssigneeId != previousAssignee && req.AssigneeId is { } a)
                await notify.NotifyAsync(task.TenantId, a, "Yeni görev", $"{type.Name}: {task.Title}", task.Id);
            await notify.TaskChangedAsync(task.TenantId, dto, previousAssignee);
            return Results.Ok(dto);
        }).RequireAuthorization(Policies.ManageTasks);

        g.MapPost("/{id:guid}/assign", async (Guid id, AssignRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir,
            NotificationService notify) =>
        {
            var task = await db.Tasks.Include(t => t.TaskType).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();
            if (await InvalidAssignee(db, ctx.User, req.AssigneeId))
                return Results.BadRequest(new ApiError("Atanan kullanıcı bu şirkette bir saha çalışanı değil."));

            var previous = task.AssigneeId;
            var ev = task.Assign(req.AssigneeId, ctx.User.UserId());
            db.TaskEvents.Add(ev);
            await db.SaveChangesAsync();

            var dto = task.ToDto(await dir.NamesAsync(task.TenantId));
            if (req.AssigneeId is { } a && a != previous)
                await notify.NotifyAsync(task.TenantId, a, "Yeni görev", $"{task.TaskType!.Name}: {task.Title}", task.Id);
            if (previous is { } p && p != req.AssigneeId)
                await notify.NotifyAsync(task.TenantId, p, "Görev geri alındı", task.Title, task.Id);
            await notify.TaskChangedAsync(task.TenantId, dto, previous);
            return Results.Ok(dto);
        }).RequireAuthorization(Policies.ManageTasks);

        g.MapPost("/{id:guid}/status", async (Guid id, ChangeStatusRequest req, HttpContext ctx, WfmDbContext db,
            UserDirectory dir, NotificationService notify) =>
        {
            var user = ctx.User;
            var task = await db.Tasks.Include(t => t.TaskType).Include(t => t.Attachments).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            var isManager = user.HasPolicy(Policies.ManageTasks);
            var isAssignee = task.AssigneeId == user.UserId();
            if (!isManager && !(isAssignee && TaskStatusFlow.IsFieldWorkerTransition(req.Status)))
                return Results.Json(new ApiError("Bu işlem için yetkiniz yok."), statusCode: 403);

            // Offline tekrar gönderimi: görev zaten istenen durumdaysa başarı dön (idempotent).
            if (task.Status == req.Status)
                return Results.Ok(task.ToDto(await dir.NamesAsync(task.TenantId)));

            if (req.Status == WorkTaskStatus.Completed)
            {
                foreach (var kv in req.CustomFieldValues ?? [])
                    if (task.TaskType!.Fields.Any(f => f.Key == kv.Key && f.FilledOnCompletion))
                        task.CustomFieldValues[kv.Key] = kv.Value;
                task.CustomFieldValues = new(task.CustomFieldValues);

                double? distance = req.Latitude is { } lat && req.Longitude is { } lng
                    ? Geo.DistanceMeters(lat, lng, task.Latitude, task.Longitude) : null;
                var errors = TaskRules.ValidateCompletion(task.TaskType!, task.CustomFieldValues,
                    task.Attachments.Count(a => a.Kind == AttachmentKind.Photo),
                    task.Attachments.Any(a => a.Kind == AttachmentKind.Signature), req.Note, distance);
                if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
                task.CompletionNote = req.Note;
            }

            var ev = task.ChangeStatus(req.Status, user.UserId(), req.Note, req.Latitude, req.Longitude);
            if (req.ClientTimestamp is { } ts) ev.CreatedAt = ts.ToUniversalTime();
            db.TaskEvents.Add(ev);
            await db.SaveChangesAsync();

            var names = await dir.NamesAsync(task.TenantId);
            var dto = task.ToDto(names);
            if (isManager && !isAssignee && task.AssigneeId is { } a &&
                req.Status is WorkTaskStatus.Cancelled or WorkTaskStatus.Assigned)
                await notify.NotifyAsync(task.TenantId, a, req.Status == WorkTaskStatus.Cancelled ? "Görev iptal edildi" : "Görev güncellendi",
                    task.Title, task.Id);
            if (!isManager && req.Status is WorkTaskStatus.Rejected or WorkTaskStatus.Failed)
                await notify.NotifyAsync(task.TenantId, task.CreatedById,
                    req.Status == WorkTaskStatus.Rejected ? "Görev reddedildi" : "Görev yapılamadı",
                    $"{names.GetValueOrDefault(user.UserId())}: {task.Title}{(string.IsNullOrEmpty(req.Note) ? "" : " – " + req.Note)}", task.Id);
            await notify.TaskChangedAsync(task.TenantId, dto);
            return Results.Ok(dto);
        });

        g.MapDelete("/{id:guid}", async (Guid id, WfmDbContext db, IFileStorage files) =>
        {
            var task = await db.Tasks.Include(t => t.Attachments).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();
            if (task.Status != WorkTaskStatus.Draft && task.Status != WorkTaskStatus.Cancelled)
                return Results.BadRequest(new ApiError("Yalnızca taslak veya iptal edilmiş görevler silinebilir."));
            foreach (var a in task.Attachments) await files.DeleteAsync(a.StoragePath);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageTasks);
    }

    /// <summary>Saha çalışanı yalnızca kendisine atanmış görevleri görür.</summary>
    public static IQueryable<WorkTask> Scope(IQueryable<WorkTask> q, ClaimsPrincipal user)
    {
        if (user.HasPolicy(Policies.ViewTracking)) return q;
        var uid = user.UserId();
        return q.Where(t => t.AssigneeId == uid);
    }

    public static Task<WorkTask?> LoadDetail(IQueryable<WorkTask> q, Guid id) =>
        q.Include(t => t.TaskType).Include(t => t.Events).Include(t => t.Attachments)
            .AsSplitQuery().FirstOrDefaultAsync(t => t.Id == id);

    private static List<string> Validate(SaveWorkTaskRequest req, TaskType type)
    {
        var errors = TaskRules.ValidateCreationFields(type, req.CustomFieldValues);
        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add("Başlık zorunlu.");
        if (req.Latitude is < -90 or > 90 || req.Longitude is < -180 or > 180 || (req.Latitude == 0 && req.Longitude == 0))
            errors.Add("Geçerli bir konum seçin.");
        if (req.ScheduledStart > req.ScheduledEnd) errors.Add("Bitiş zamanı başlangıçtan önce olamaz.");
        return errors;
    }

    private static async Task<bool> InvalidAssignee(WfmDbContext db, ClaimsPrincipal user, Guid? assigneeId)
    {
        if (assigneeId is null) return false;
        var tid = user.TenantId();
        return !await (from u in db.Users
                       join ur in db.UserRoles on u.Id equals ur.UserId
                       join r in db.Roles on ur.RoleId equals r.Id
                       where u.Id == assigneeId && u.TenantId == tid && u.IsActive && r.Name == Roles.FieldWorker
                       select u.Id).AnyAsync();
    }

    private static void Apply(WorkTask t, SaveWorkTaskRequest r)
    {
        t.TaskTypeId = r.TaskTypeId;
        t.Title = r.Title.Trim();
        t.Description = r.Description;
        t.Priority = r.Priority;
        t.TeamId = r.TeamId;
        t.Address = r.Address;
        t.Latitude = r.Latitude;
        t.Longitude = r.Longitude;
        t.CustomerName = r.CustomerName;
        t.CustomerPhone = r.CustomerPhone;
        t.ScheduledStart = r.ScheduledStart?.ToUniversalTime();
        t.ScheduledEnd = r.ScheduledEnd?.ToUniversalTime();
        // Saha çalışanının tamamlamada girdiği değerleri koru.
        var merged = new Dictionary<string, string?>(t.CustomFieldValues);
        foreach (var kv in r.CustomFieldValues) merged[kv.Key] = kv.Value;
        t.CustomFieldValues = merged;
    }
}

public record TaskQueryParams(WorkTaskStatus[]? Statuses, Guid? AssigneeId, Guid? TaskTypeId, DateTime? From, DateTime? To,
    string? Search, bool OnlyOpen = false, int? Page = 1, int? PageSize = 50);

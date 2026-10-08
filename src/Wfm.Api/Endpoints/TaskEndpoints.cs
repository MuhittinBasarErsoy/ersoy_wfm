using System.Security.Claims;
using System.Security.Cryptography;
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
            if (q.OnlyOpen || q.Overdue) query = query.Where(t => t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled && t.Status != WorkTaskStatus.Failed);
            if (q.Overdue)
            {
                var now = DateTime.UtcNow;
                query = query.Where(t => t.ScheduledEnd != null && t.ScheduledEnd < now);
            }
            if (q.AssigneeId is { } a) query = query.Where(t => t.AssigneeId == a);
            if (q.TaskTypeId is { } tt) query = query.Where(t => t.TaskTypeId == tt);
            if (!string.IsNullOrWhiteSpace(q.Stage)) query = query.Where(t => t.Stage == q.Stage);
            if (q.From is { } from) query = query.Where(t => (t.ScheduledStart ?? t.CreatedAt) >= from);
            if (q.To is { } to) query = query.Where(t => (t.ScheduledStart ?? t.CreatedAt) < to);
            if (!string.IsNullOrWhiteSpace(q.Search))
                query = query.Where(t => t.Title.Contains(q.Search) || t.Address.Contains(q.Search) || (t.CustomerName != null && t.CustomerName.Contains(q.Search)));

            var total = await query.CountAsync();
            var page = Math.Max(1, q.Page ?? 1);
            var size = Math.Clamp(q.PageSize ?? 50, 1, 500);
            var items = await Sort(query, db, q.Sort, q.Desc ?? false).Skip((page - 1) * size).Take(size).ToListAsync();
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
                await notify.NotifyAsync(task.TenantId, assignee, "Yeni görev", $"{type.Name}: {task.Title}", task.Id, NotificationKinds.Assigned);
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
            if (task.TaskTypeId != req.TaskTypeId) task.Stage = null; // eski tipin aşaması yeni tipte geçersiz
            Apply(task, req);
            task.TaskType = type;
            if (req.AssigneeId != previousAssignee) task.Assign(req.AssigneeId, ctx.User.UserId());
            await db.SaveChangesAsync();

            var dto = task.ToDto(await dir.NamesAsync(task.TenantId));
            if (req.AssigneeId != previousAssignee && req.AssigneeId is { } a)
                await notify.NotifyAsync(task.TenantId, a, "Yeni görev", $"{type.Name}: {task.Title}", task.Id, NotificationKinds.Assigned);
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
                await notify.NotifyAsync(task.TenantId, a, "Yeni görev", $"{task.TaskType!.Name}: {task.Title}", task.Id, NotificationKinds.Assigned);
            if (previous is { } p && p != req.AssigneeId)
                await notify.NotifyAsync(task.TenantId, p, "Görev geri alındı", task.Title, task.Id, NotificationKinds.Unassigned);
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
                    task.Title, task.Id, req.Status == WorkTaskStatus.Cancelled ? NotificationKinds.Cancelled : NotificationKinds.Assigned);
            if (!isManager && req.Status is WorkTaskStatus.Rejected or WorkTaskStatus.Failed)
                await notify.NotifyAsync(task.TenantId, task.CreatedById,
                    req.Status == WorkTaskStatus.Rejected ? "Görev reddedildi" : "Görev yapılamadı",
                    $"{names.GetValueOrDefault(user.UserId())}: {task.Title}{(string.IsNullOrEmpty(req.Note) ? "" : " – " + req.Note)}", task.Id,
                    req.Status == WorkTaskStatus.Rejected ? NotificationKinds.Rejected : NotificationKinds.Failed);
            await notify.TaskChangedAsync(task.TenantId, dto);
            return Results.Ok(dto);
        });

        g.MapPost("/{id:guid}/stage", async (Guid id, ChangeStageRequest req, HttpContext ctx, WfmDbContext db,
            UserDirectory dir, NotificationService notify) =>
        {
            var user = ctx.User;
            var task = await db.Tasks.Include(t => t.TaskType).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            var isManager = user.HasPolicy(Policies.ManageTasks);
            var isAssignee = task.AssigneeId == user.UserId();
            if (!isManager && !isAssignee)
                return Results.Json(new ApiError("Bu işlem için yetkiniz yok."), statusCode: 403);

            var stage = string.IsNullOrWhiteSpace(req.Stage) ? null : req.Stage.Trim();
            // Offline tekrar gönderimi: aşama zaten istenen değerdeyse başarı dön (idempotent).
            if (task.Stage == stage)
                return Results.Ok(task.ToDto(await dir.NamesAsync(task.TenantId)));

            var ev = task.SetStage(stage, user.UserId(), req.Note);
            if (req.ClientTimestamp is { } ts) ev.CreatedAt = ts.ToUniversalTime();
            db.TaskEvents.Add(ev);
            await db.SaveChangesAsync();

            var names = await dir.NamesAsync(task.TenantId);
            var dto = task.ToDto(names);
            if (!isManager && stage is not null)
                await notify.NotifyAsync(task.TenantId, task.CreatedById, $"Aşama: {stage}",
                    $"{names.GetValueOrDefault(user.UserId())}: {task.Title}", task.Id, NotificationKinds.StageChanged);
            else if (isManager && !isAssignee && task.AssigneeId is { } a && stage is not null)
                await notify.NotifyAsync(task.TenantId, a, $"Aşama: {stage}", task.Title, task.Id, NotificationKinds.StageChanged);
            await notify.TaskChangedAsync(task.TenantId, dto);
            return Results.Ok(dto);
        });

        // ---------- Kayıtlı filtreler (görev listesi) ----------
        var filters = app.MapGroup("/api/saved-filters").WithTags("SavedFilters").RequireAuthorization(Policies.ViewTracking);
        filters.MapGet("/", async (HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var uid = ctx.User.UserId();
            var names = await dir.NamesAsync(ctx.User.TenantId());
            var list = await db.SavedFilters.Where(f => f.UserId == uid || f.IsShared).OrderBy(f => f.Name).ToListAsync();
            return list.Select(f => new SavedFilterDto(f.Id, f.Name, f.Query, f.IsShared, f.UserId == uid, names.GetValueOrDefault(f.UserId))).ToList();
        });
        filters.MapPost("/", async (SaveFilterRequest req, HttpContext ctx, WfmDbContext db) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) return Results.BadRequest(new ApiError("Filtre adı 1-100 karakter olmalı."));
            if ((req.Query ?? "").Length > 2000) return Results.BadRequest(new ApiError("Filtre çok uzun."));
            var uid = ctx.User.UserId();
            // Aynı adlı kendi filtresi varsa güncellenir.
            var f = await db.SavedFilters.FirstOrDefaultAsync(x => x.UserId == uid && x.Name == name);
            if (f is null) db.SavedFilters.Add(f = new SavedFilter { UserId = uid, Name = name });
            f.Query = req.Query ?? "";
            f.IsShared = req.IsShared;
            await db.SaveChangesAsync();
            return Results.Ok(new SavedFilterDto(f.Id, f.Name, f.Query, f.IsShared, true, null));
        });
        filters.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            var f = await db.SavedFilters.FirstOrDefaultAsync(x => x.Id == id);
            if (f is null) return Results.NotFound();
            if (f.UserId != uid && !ctx.User.HasPolicy(Policies.ManageUsers))
                return Results.Json(new ApiError("Yalnızca kendi filtrenizi silebilirsiniz."), statusCode: 403);
            db.SavedFilters.Remove(f);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- Müşteri takip linki ----------
        g.MapPost("/{id:guid}/tracking-link", async (Guid id, WfmDbContext db) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();
            if (task.TrackingToken is null)
            {
                task.TrackingToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                await db.SaveChangesAsync();
            }
            return Results.Ok(new TrackingLinkDto(task.TrackingToken));
        }).RequireAuthorization(Policies.ManageTasks);

        // ---------- Yorumlar ----------
        g.MapGet("/{id:guid}/comments", async (Guid id, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            if (!await Scope(db.Tasks, ctx.User).AnyAsync(t => t.Id == id)) return Results.NotFound();
            var names = await dir.NamesAsync(ctx.User.TenantId());
            var list = await db.TaskComments.Where(c => c.WorkTaskId == id).OrderBy(c => c.CreatedAt).Take(500).ToListAsync();
            return Results.Ok(list.Select(c => c.ToDto(names)).ToList());
        });

        g.MapPost("/{id:guid}/comments", async (Guid id, AddCommentRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir,
            NotificationService notify) =>
        {
            var user = ctx.User;
            var task = await Scope(db.Tasks, user).FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();
            if (!user.HasPolicy(Policies.ManageTasks) && task.AssigneeId != user.UserId())
                return Results.Json(new ApiError("Bu göreve yazamazsınız."), statusCode: 403);
            var body = req.Body?.Trim() ?? "";
            if (body.Length is 0 or > 2000) return Results.BadRequest(new ApiError("Mesaj 1-2000 karakter olmalı."));

            var comment = new TaskComment { TenantId = task.TenantId, WorkTaskId = id, UserId = user.UserId(), Body = body };
            db.TaskComments.Add(comment);
            await db.SaveChangesAsync();

            var names = await dir.NamesAsync(task.TenantId);
            var dto = comment.ToDto(names);
            var preview = body.Length > 80 ? body[..80] + "…" : body;
            // Saha çalışanı yazdıysa görevi oluşturana, yönetici yazdıysa atanan çalışana bildirim.
            var recipient = task.AssigneeId == user.UserId() ? task.CreatedById : task.AssigneeId;
            if (recipient is { } r && r != user.UserId())
                await notify.NotifyAsync(task.TenantId, r, $"Mesaj: {task.Title}", $"{dto.UserName}: {preview}", task.Id, NotificationKinds.Comment);
            await notify.CommentAddedAsync(task.TenantId, dto, task.AssigneeId);
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

    private static IQueryable<WorkTask> Sort(IQueryable<WorkTask> q, WfmDbContext db, string? sort, bool desc)
    {
        switch (sort?.ToLowerInvariant())
        {
            case "scheduled":
                return desc ? q.OrderByDescending(t => t.ScheduledStart ?? t.CreatedAt) : q.OrderBy(t => t.ScheduledStart ?? t.CreatedAt);
            case "created":
                return desc ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt);
            case "updated":
                return desc ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt);
            case "title":
                return desc ? q.OrderByDescending(t => t.Title) : q.OrderBy(t => t.Title);
            case "status":
                return desc ? q.OrderByDescending(t => t.Status).ThenBy(t => t.ScheduledStart ?? t.CreatedAt)
                            : q.OrderBy(t => t.Status).ThenBy(t => t.ScheduledStart ?? t.CreatedAt);
            case "assignee":
                // Atanmamış görevler her iki yönde de en sonda.
                var joined = from t in q
                             join u in db.Users on t.AssigneeId equals (Guid?)u.Id into gj
                             from u in gj.DefaultIfEmpty()
                             select new { t, Name = u == null ? null : u.FullName };
                return (desc ? joined.OrderBy(x => x.Name == null).ThenByDescending(x => x.Name)
                             : joined.OrderBy(x => x.Name == null).ThenBy(x => x.Name)).Select(x => x.t);
            default:
                return desc || sort is null
                    ? q.OrderByDescending(t => t.Priority).ThenBy(t => t.ScheduledStart ?? t.CreatedAt)
                    : q.OrderBy(t => t.Priority).ThenBy(t => t.ScheduledStart ?? t.CreatedAt);
        }
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
        if (req.Latitude is < -90 or > 90 || req.Longitude is < -180 or > 180)
            errors.Add("Geçerli bir konum seçin.");
        else if (type.RequiresVisit && req.Latitude == 0 && req.Longitude == 0)
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
        var end = r.ScheduledEnd?.ToUniversalTime();
        if (end != t.ScheduledEnd) t.OverdueNotifiedAt = null; // yeni bitiş saati için yeniden bildirilebilir
        t.ScheduledEnd = end;
        // Saha çalışanının tamamlamada girdiği değerleri koru.
        var merged = new Dictionary<string, string?>(t.CustomFieldValues);
        foreach (var kv in r.CustomFieldValues) merged[kv.Key] = kv.Value;
        t.CustomFieldValues = merged;
    }
}

public record TaskQueryParams(WorkTaskStatus[]? Statuses, Guid? AssigneeId, Guid? TaskTypeId, string? Stage, DateTime? From, DateTime? To,
    string? Search, bool OnlyOpen = false, bool Overdue = false, string? Sort = null, bool? Desc = false, int? Page = 1, int? PageSize = 50);

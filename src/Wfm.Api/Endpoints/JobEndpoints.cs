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

/// <summary>Çok adımlı işler ve iş akışı şablonları.</summary>
public static class JobEndpoints
{
    private const int MaxSteps = 50;

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- Şablonlar ----------
        var templates = app.MapGroup("/api/job-templates").WithTags("JobTemplates").RequireAuthorization(Policies.ViewTracking);

        templates.MapGet("/", async (WfmDbContext db) =>
            (await db.JobTemplates.OrderBy(t => t.Name).ToListAsync()).Select(t => t.ToDto()).ToList());

        templates.MapGet("/{id:guid}", async (Guid id, WfmDbContext db) =>
            await db.JobTemplates.FirstOrDefaultAsync(t => t.Id == id) is { } t ? Results.Ok(t.ToDto()) : Results.NotFound());

        templates.MapPost("/", async (SaveJobTemplateRequest req, HttpContext ctx, WfmDbContext db) =>
        {
            var errors = await ValidateTemplateAsync(req, db, ctx.User);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
            var t = new JobTemplate();
            ApplyTemplate(t, req);
            db.JobTemplates.Add(t);
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageTasks);

        templates.MapPut("/{id:guid}", async (Guid id, SaveJobTemplateRequest req, HttpContext ctx, WfmDbContext db) =>
        {
            var t = await db.JobTemplates.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            var errors = await ValidateTemplateAsync(req, db, ctx.User);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
            ApplyTemplate(t, req);
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageTasks);

        templates.MapDelete("/{id:guid}", async (Guid id, WfmDbContext db) =>
        {
            var t = await db.JobTemplates.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            db.JobTemplates.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageTasks);

        // ---------- İşler ----------
        var g = app.MapGroup("/api/jobs").WithTags("Jobs").RequireAuthorization(Policies.ViewTracking);

        g.MapGet("/", async (JobStatus? status, string? search, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var q = db.Jobs.Include(j => j.Tasks).AsSplitQuery().AsQueryable();
            if (status is { } s) q = q.Where(j => j.Status == s);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(j => j.Title.Contains(search) || (j.CustomerName != null && j.CustomerName.Contains(search)));
            var list = await q.OrderBy(j => j.Status).ThenByDescending(j => j.UpdatedAt).Take(500).ToListAsync();
            var names = await dir.NamesAsync(ctx.User.TenantId());
            return list.Select(j => j.ToDto(names)).ToList();
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, JobProgressService jobs, UserDirectory dir) =>
        {
            var job = await jobs.LoadAsync(id);
            return job is null ? Results.NotFound() : Results.Ok(job.ToDetailDto(await dir.NamesAsync(ctx.User.TenantId())));
        });

        g.MapPost("/", async (SaveJobRequest req, HttpContext ctx, WfmDbContext db, JobProgressService jobs, UserDirectory dir,
            NotificationService notify) =>
        {
            var types = await db.TaskTypes.ToDictionaryAsync(t => t.Id);
            var errors = await ValidateStepsAsync(req.Steps, types, db, ctx.User);
            if (string.IsNullOrWhiteSpace(req.Title)) errors.Add("İş başlığı zorunlu.");
            if (req.Latitude is < -90 or > 90 || req.Longitude is < -180 or > 180 ||
                (req.Latitude == 0 && req.Longitude == 0 && req.Steps.Any(s => types.GetValueOrDefault(s.TaskTypeId)?.RequiresVisit == true)))
                errors.Add("Sahada yapılacak adımlar için geçerli bir konum seçin.");
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));

            var userId = ctx.User.UserId();
            var job = new Job
            {
                TenantId = ctx.User.TenantId(),
                CreatedById = userId,
                Title = req.Title.Trim(),
                Description = req.Description,
                TemplateId = req.TemplateId,
                CustomerName = req.CustomerName,
                CustomerPhone = req.CustomerPhone,
                Address = req.Address,
                Latitude = req.Latitude,
                Longitude = req.Longitude
            };
            var orders = OrderMap(req.Steps.Select(s => s.Order));
            foreach (var s in req.Steps)
                job.Tasks.Add(NewStep(job, s, orders[s.Order], req.Priority, req.ScheduledEnd, userId));
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            await jobs.AdvanceAsync(job.Id, userId);
            var names = await dir.NamesAsync(job.TenantId);
            foreach (var t in job.Tasks.Where(t => t.AssigneeId is null))
                await notify.TaskChangedAsync(job.TenantId, t.ToDto(names));
            return Results.Ok(job.ToDetailDto(names));
        }).RequireAuthorization(Policies.ManageTasks);

        // Akışı düzenle: yalnızca sırası gelmemiş adımlar değiştirilebilir/eklenir/silinir; başlamış adımlar olduğu gibi kalır.
        g.MapPut("/{id:guid}/steps", async (Guid id, UpdateJobStepsRequest req, HttpContext ctx, WfmDbContext db,
            JobProgressService jobs, UserDirectory dir) =>
        {
            var job = await jobs.LoadAsync(id);
            if (job is null) return Results.NotFound();
            if (!job.IsOpen) return Results.BadRequest(new ApiError("Kapanmış iş düzenlenemez."));

            var types = await db.TaskTypes.ToDictionaryAsync(t => t.Id);
            var editable = req.Steps.Where(s => s.TaskId is not { } tid || job.Tasks.FirstOrDefault(t => t.Id == tid) is not { } t || job.IsPending(t)).ToList();
            var errors = await ValidateStepsAsync(editable, types, db, ctx.User, allowEmpty: true);
            if (editable.Any(s => s.Order < job.CurrentOrder))
                errors.Add("Yeni adımlar yürüyen sıradan önceye konamaz.");
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));

            var userId = ctx.User.UserId();
            var keep = editable.Where(s => s.TaskId is not null).Select(s => s.TaskId!.Value).ToHashSet();
            foreach (var t in job.Tasks.Where(t => job.IsPending(t) && !keep.Contains(t.Id)).ToList())
            {
                job.Tasks.Remove(t);
                db.Tasks.Remove(t);
            }

            var template = job.Tasks.FirstOrDefault();
            foreach (var s in editable)
            {
                var existing = s.TaskId is { } tid ? job.Tasks.FirstOrDefault(t => t.Id == tid) : null;
                if (existing is null)
                {
                    var step = NewStep(job, s, s.Order, template?.Priority ?? TaskPriority.Normal, template?.ScheduledEnd, userId);
                    job.Tasks.Add(step);
                    db.Tasks.Add(step);
                }
                else
                {
                    existing.StepOrder = s.Order;
                    existing.TaskTypeId = s.TaskTypeId;
                    existing.TaskType = types[s.TaskTypeId];
                    existing.Title = s.Title.Trim();
                    existing.Description = s.Description;
                    existing.PlannedAssigneeId = s.AssigneeId;
                }
            }

            // Sıra numaralarını boşluksuz yap (1, 2, 3...).
            var orders = OrderMap(job.Tasks.Where(t => t.StepOrder is not null).Select(t => t.StepOrder!.Value).Append(job.CurrentOrder));
            foreach (var t in job.Tasks.Where(t => t.StepOrder is not null)) t.StepOrder = orders[t.StepOrder!.Value];
            job.CurrentOrder = orders[job.CurrentOrder];
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await jobs.AdvanceAsync(job.Id, userId); // yürüyen sıraya eklenen paralel adım hemen başlar
            return Results.Ok(job.ToDetailDto(await dir.NamesAsync(job.TenantId)));
        }).RequireAuthorization(Policies.ManageTasks);

        // Beklemedeki işte sorunlu adımı yeniden başlat: reddedilen adım yeniden atanır, başarısız adımın yerine yeni deneme açılır.
        g.MapPost("/{id:guid}/steps/{taskId:guid}/retry", async (Guid id, Guid taskId, AssignRequest req, HttpContext ctx,
            WfmDbContext db, JobProgressService jobs, UserDirectory dir, NotificationService notify) =>
        {
            var job = await jobs.LoadAsync(id);
            var task = job?.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (job is null || task is null) return Results.NotFound();
            if (task.Status is not (WorkTaskStatus.Failed or WorkTaskStatus.Rejected) || task.StepOrder is null)
                return Results.BadRequest(new ApiError("Yalnızca reddedilen veya yapılamayan adım yeniden başlatılabilir."));
            if (req.AssigneeId is null || await TaskEndpoints.InvalidAssignee(db, ctx.User, req.AssigneeId))
                return Results.BadRequest(new ApiError("Geçerli bir saha çalışanı seçin."));

            var userId = ctx.User.UserId();
            var target = task;
            if (task.Status == WorkTaskStatus.Failed)
            {
                target = NewStep(job, new JobStepInput { TaskTypeId = task.TaskTypeId, Title = task.Title, Description = task.Description },
                    task.StepOrder.Value, task.Priority, task.ScheduledEnd, userId);
                target.TaskType = task.TaskType;
                target.CustomFieldValues = task.TaskType!.Fields.Where(f => !f.FilledOnCompletion)
                    .Where(f => task.CustomFieldValues.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => task.CustomFieldValues[f.Key]);
                task.StepOrder = null; // eski deneme geçmişte kalır
                job.Tasks.Add(target);
                db.Tasks.Add(target);
            }
            target.PlannedAssigneeId = req.AssigneeId;
            db.TaskEvents.Add(target.Assign(req.AssigneeId, userId));
            await db.SaveChangesAsync();
            await jobs.AdvanceAsync(job.Id, userId);

            var names = await dir.NamesAsync(job.TenantId);
            await notify.NotifyAsync(job.TenantId, req.AssigneeId.Value, "Yeni görev", $"{job.Title}: {target.Title}", target.Id, NotificationKinds.Assigned);
            await notify.TaskChangedAsync(job.TenantId, target.ToDto(names), task.AssigneeId);
            return Results.Ok(job.ToDetailDto(names));
        }).RequireAuthorization(Policies.ManageTasks);

        // Beklemedeki işte sorunlu adımı atla ve akışa devam et.
        g.MapPost("/{id:guid}/steps/{taskId:guid}/skip", async (Guid id, Guid taskId, HttpContext ctx, WfmDbContext db,
            JobProgressService jobs, UserDirectory dir) =>
        {
            var job = await jobs.LoadAsync(id);
            var task = job?.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (job is null || task is null) return Results.NotFound();
            if (task.Status is not (WorkTaskStatus.Failed or WorkTaskStatus.Rejected))
                return Results.BadRequest(new ApiError("Yalnızca reddedilen veya yapılamayan adım atlanabilir."));

            var userId = ctx.User.UserId();
            if (task.Status == WorkTaskStatus.Rejected)
                db.TaskEvents.Add(task.ChangeStatus(WorkTaskStatus.Cancelled, userId, "Adım atlandı"));
            else
                task.StepOrder = null;
            await db.SaveChangesAsync();
            await jobs.AdvanceAsync(job.Id, userId);
            return Results.Ok(job.ToDetailDto(await dir.NamesAsync(job.TenantId)));
        }).RequireAuthorization(Policies.ManageTasks);

        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext ctx, WfmDbContext db, JobProgressService jobs,
            UserDirectory dir, NotificationService notify) =>
        {
            var job = await jobs.LoadAsync(id);
            if (job is null) return Results.NotFound();
            if (!job.IsOpen) return Results.BadRequest(new ApiError("İş zaten kapanmış."));

            var userId = ctx.User.UserId();
            var cancelled = new List<WorkTask>();
            foreach (var t in job.Tasks.Where(t => t.IsOpen))
            {
                db.TaskEvents.Add(t.ChangeStatus(WorkTaskStatus.Cancelled, userId, "İş iptal edildi"));
                cancelled.Add(t);
            }
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var names = await dir.NamesAsync(job.TenantId);
            foreach (var t in cancelled)
            {
                if (t.AssigneeId is { } a && a != userId)
                    await notify.NotifyAsync(job.TenantId, a, "Görev iptal edildi", $"{job.Title}: {t.Title}", t.Id, NotificationKinds.Cancelled);
                await notify.TaskChangedAsync(job.TenantId, t.ToDto(names));
            }
            return Results.Ok(job.ToDetailDto(names));
        }).RequireAuthorization(Policies.ManageTasks);
    }

    /// <summary>Görev detayları için iş bağlamı (iş adı, adım numarası, önceki adımların notları).</summary>
    public static async Task<Dictionary<Guid, TaskJobContextDto>> LoadContextsAsync(WfmDbContext db, IEnumerable<WorkTask> tasks,
        IReadOnlyDictionary<Guid, string> names)
    {
        var jobIds = tasks.Where(t => t.JobId is not null).Select(t => t.JobId!.Value).Distinct().ToList();
        if (jobIds.Count == 0) return [];
        var jobs = await db.Jobs.Where(j => jobIds.Contains(j.Id))
            .Select(j => new { j.Id, j.Title }).ToDictionaryAsync(j => j.Id);
        var steps = await db.Tasks.Where(t => t.JobId != null && jobIds.Contains(t.JobId.Value) && t.StepOrder != null)
            .Select(t => new { t.JobId, t.StepOrder, t.Title, t.AssigneeId, t.Status, t.CompletionNote, t.CompletedAt })
            .ToListAsync();

        var result = new Dictionary<Guid, TaskJobContextDto>();
        foreach (var t in tasks)
        {
            if (t.JobId is not { } jid || !jobs.TryGetValue(jid, out var job)) continue;
            var own = steps.Where(s => s.JobId == jid).ToList();
            var order = t.StepOrder ?? 0;
            var previous = own.Where(s => s.StepOrder < order).OrderBy(s => s.StepOrder)
                .Select(s => new JobStepSummaryDto(s.StepOrder!.Value, s.Title,
                    s.AssigneeId is { } a ? names.GetValueOrDefault(a) : null, s.Status, s.CompletionNote, s.CompletedAt))
                .ToList();
            result[t.Id] = new TaskJobContextDto(jid, job.Title, order, own.Select(s => s.StepOrder!.Value).DefaultIfEmpty(order).Max(), previous);
        }
        return result;
    }

    private static WorkTask NewStep(Job job, JobStepInput s, int order, TaskPriority priority, DateTime? scheduledEnd, Guid userId) => new()
    {
        TenantId = job.TenantId,
        JobId = job.Id,
        StepOrder = order,
        CreatedById = userId,
        TaskTypeId = s.TaskTypeId,
        Title = s.Title.Trim(),
        Description = s.Description,
        Priority = priority,
        PlannedAssigneeId = s.AssigneeId,
        Address = job.Address,
        Latitude = job.Latitude,
        Longitude = job.Longitude,
        CustomerName = job.CustomerName,
        CustomerPhone = job.CustomerPhone,
        ScheduledEnd = scheduledEnd?.ToUniversalTime()
    };

    /// <summary>Verilen sıra numaralarını 1'den başlayan boşluksuz numaralara eşler (aynı numaralar aynı kalır).</summary>
    private static Dictionary<int, int> OrderMap(IEnumerable<int> orders) =>
        orders.Distinct().Order().Select((o, i) => (o, i)).ToDictionary(x => x.o, x => x.i + 1);

    private static async Task<List<string>> ValidateStepsAsync(List<JobStepInput> steps, Dictionary<Guid, TaskType> types,
        WfmDbContext db, ClaimsPrincipal user, bool allowEmpty = false)
    {
        var errors = new List<string>();
        if (steps.Count == 0 && !allowEmpty) errors.Add("En az bir adım ekleyin.");
        if (steps.Count > MaxSteps) errors.Add($"Bir işte en fazla {MaxSteps} adım olabilir.");
        if (steps.Any(s => string.IsNullOrWhiteSpace(s.Title))) errors.Add("Her adımın bir başlığı olmalı.");
        if (steps.Any(s => s.Order < 1)) errors.Add("Sıra numarası 1'den küçük olamaz.");
        if (steps.Any(s => !types.ContainsKey(s.TaskTypeId))) errors.Add("Adımlardan birinin görev tipi bulunamadı.");
        foreach (var a in steps.Select(s => s.AssigneeId).OfType<Guid>().Distinct())
            if (await TaskEndpoints.InvalidAssignee(db, user, a))
            {
                errors.Add("Adımlara yalnızca bu şirketin aktif saha çalışanları atanabilir.");
                break;
            }
        return errors;
    }

    private static async Task<List<string>> ValidateTemplateAsync(SaveJobTemplateRequest req, WfmDbContext db, ClaimsPrincipal user)
    {
        var types = await db.TaskTypes.ToDictionaryAsync(t => t.Id);
        var errors = await ValidateStepsAsync(req.Steps ?? [], types, db, user);
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 200) errors.Add("Şablon adı 1-200 karakter olmalı.");
        return errors;
    }

    private static void ApplyTemplate(JobTemplate t, SaveJobTemplateRequest req)
    {
        t.Name = req.Name.Trim();
        t.Description = req.Description;
        var orders = OrderMap(req.Steps.Select(s => s.Order));
        t.Steps = req.Steps.OrderBy(s => s.Order).Select(s => new JobTemplateStep
        {
            Order = orders[s.Order], TaskTypeId = s.TaskTypeId, Title = s.Title.Trim(), Description = s.Description,
            DefaultAssigneeId = s.AssigneeId
        }).ToList();
    }
}

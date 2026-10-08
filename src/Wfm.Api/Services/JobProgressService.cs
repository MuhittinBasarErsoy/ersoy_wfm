using Microsoft.EntityFrameworkCore;
using Wfm.Application.Contracts;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

/// <summary>
/// Çok adımlı işlerin akışını yürütür: bir adımın durumu değişince işi ilerletir, sırası gelen adımları
/// planlanan çalışanlara atar ve ilgili kişileri bilgilendirir.
/// </summary>
public class JobProgressService(WfmDbContext db, NotificationService notify, UserDirectory dir)
{
    public Task<Job?> LoadAsync(Guid jobId) =>
        db.Jobs.Include(j => j.Tasks).ThenInclude(t => t.TaskType).AsSplitQuery().FirstOrDefaultAsync(j => j.Id == jobId);

    /// <summary>Görev bir işin henüz sırası gelmemiş adımı mı? (Atama bu durumda yalnızca planlanan kişiyi değiştirir.)</summary>
    public async Task<bool> IsPendingStepAsync(WorkTask task)
    {
        if (task.JobId is not { } jobId || task.StepOrder is null || task.Status != WorkTaskStatus.Draft) return false;
        var current = await db.Jobs.Where(j => j.Id == jobId).Select(j => (int?)j.CurrentOrder).FirstOrDefaultAsync();
        return current is { } c && task.StepOrder > c;
    }

    /// <summary>
    /// İşi ilerletir. Tekrar çağrılması güvenlidir (offline tekrar gönderimleri, paralel adımların aynı anda bitmesi).
    /// </summary>
    public async Task AdvanceAsync(Guid? jobId, Guid actorId)
    {
        if (jobId is not { } id) return;

        for (var attempt = 0; ; attempt++)
        {
            var job = await LoadAsync(id);
            if (job is null) return;

            var result = job.Advance();
            var activated = new List<WorkTask>();
            foreach (var t in result.Activate.Where(t => t.PlannedAssigneeId is not null))
            {
                db.TaskEvents.Add(t.Assign(t.PlannedAssigneeId, actorId));
                activated.Add(t);
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // Paralel adımlar aynı anda bitti: diğer istek işi zaten ilerletmiş olabilir; güncel hâliyle tekrar dene.
                foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is Job or WorkTask or TaskEvent).ToList())
                {
                    if (e.State == EntityState.Added) e.State = EntityState.Detached;
                    else await e.ReloadAsync();
                }
                continue;
            }

            await NotifyAsync(job, result, activated);
            return;
        }
    }

    private async Task NotifyAsync(Job job, JobAdvance result, List<WorkTask> activated)
    {
        if (activated.Count == 0 && result.Activate.Count == 0 && !result.PutOnHold && !result.Completed) return;
        var names = await dir.NamesAsync(job.TenantId);

        foreach (var t in activated)
        {
            await notify.NotifyAsync(job.TenantId, t.AssigneeId!.Value, "Sıra sizde", $"{job.Title}: {t.Title}", t.Id, NotificationKinds.Assigned);
            await notify.TaskChangedAsync(job.TenantId, t.ToDto(names));
        }

        // Kişisi belirlenmemiş adım: işi açan yönetici atasın.
        if (result.OrderChanged)
            foreach (var t in result.Activate.Where(t => t.AssigneeId is null))
                await notify.NotifyAsync(job.TenantId, job.CreatedById, "Adım atanmayı bekliyor", $"{job.Title}: {t.Title}", t.Id,
                    NotificationKinds.General);

        if (result.PutOnHold)
        {
            var problem = job.Tasks.First(t => t.StepOrder == job.CurrentOrder && t.Status is WorkTaskStatus.Failed or WorkTaskStatus.Rejected);
            var who = problem.AssigneeId is { } a ? names.GetValueOrDefault(a) : null;
            var what = problem.Status == WorkTaskStatus.Rejected ? "reddetti" : "yapamadı";
            await notify.NotifyAsync(job.TenantId, job.CreatedById, "İş beklemede",
                $"{job.Title}: {who ?? "Çalışan"} '{problem.Title}' adımını {what}.", problem.Id, NotificationKinds.JobOnHold);
        }

        if (result.Completed)
            await notify.NotifyAsync(job.TenantId, job.CreatedById, "İş tamamlandı", job.Title, null, NotificationKinds.JobCompleted);
    }
}

using Microsoft.EntityFrameworkCore;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

/// <summary>Planlanan bitişi geçmiş açık görevler için görevi oluşturana bir kez gecikme bildirimi gönderir.</summary>
public class OverdueMonitorService(IServiceScopeFactory scopes, ILogger<OverdueMonitorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
                var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var now = DateTime.UtcNow;
                // Arka planda istek bağlamı yok; kiracı filtresi yerine kiracı kimliği açıkça kullanılır.
                var late = await db.Tasks.IgnoreQueryFilters()
                    .Where(t => t.OverdueNotifiedAt == null && t.ScheduledEnd != null && t.ScheduledEnd < now &&
                                t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled && t.Status != WorkTaskStatus.Failed)
                    .OrderBy(t => t.ScheduledEnd).Take(200).ToListAsync(ct);
                foreach (var t in late)
                {
                    t.OverdueNotifiedAt = now;
                    await db.SaveChangesAsync(ct);
                    await notify.NotifyAsync(t.TenantId, t.CreatedById, "Görev gecikti",
                        $"{t.Title} planlanan bitiş saatini geçti.", t.Id, NotificationKinds.Overdue);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Gecikme kontrolü başarısız.");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

using Microsoft.EntityFrameworkCore;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

/// <summary>
/// Devriye turlarını yürütür (dakikada bir): aktif rotalar için bugünün ve yarının turlarını üretir, pencere açılınca ve
/// bitmeye az kala çalışana hatırlatır, pencere biten eksik turları kapatıp yöneticilere bildirir.
/// </summary>
public class PatrolSchedulerService(IServiceScopeFactory scopes, ILogger<PatrolSchedulerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await RunOnceAsync(scope.ServiceProvider, DateTime.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Devriye kontrolü başarısız.");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Tek bir kontrol turu (testler zamanı vererek doğrudan çağırır).</summary>
    public static async Task RunOnceAsync(IServiceProvider sp, DateTime now, CancellationToken ct = default)
    {
        var db = sp.GetRequiredService<WfmDbContext>();
        var notify = sp.GetRequiredService<NotificationService>();
        await GenerateAsync(db, now, ct);

        // Arka planda istek bağlamı yok; kiracı filtresi yerine kiracı kimliği açıkça kullanılır.
        var open = await db.PatrolRounds.IgnoreQueryFilters().Include(r => r.Visits)
            .Where(r => r.WindowStart <= now && (r.Status == PatrolRoundStatus.Scheduled || r.Status == PatrolRoundStatus.InProgress))
            .OrderBy(r => r.WindowStart).Take(500).ToListAsync(ct);

        foreach (var r in open)
        {
            if (r.CloseIfExpired(now))
            {
                await db.SaveChangesAsync(ct);
                await NotifyManagersAsync(db, notify, r, ct);
                continue;
            }

            if (!r.StartReminderSent)
            {
                r.StartReminderSent = true;
                if (r.Status == PatrolRoundStatus.Scheduled) r.Status = PatrolRoundStatus.InProgress;
                await db.SaveChangesAsync(ct);
                await notify.NotifyAsync(r.TenantId, r.AssigneeId, "Devriye başladı",
                    $"{r.RouteName}: {r.Checkpoints.Count} nokta, bitiş {Local(r.WindowEnd):HH:mm}.", null, NotificationKinds.PatrolStarting);
                continue;
            }

            var reminder = await db.PatrolRoutes.IgnoreQueryFilters().Where(x => x.Id == r.RouteId)
                .Select(x => (int?)x.ReminderMinutesBeforeEnd).FirstOrDefaultAsync(ct) ?? 15;
            if (!r.EndReminderSent && reminder > 0 && now >= r.WindowEnd.AddMinutes(-reminder) && r.MissingCount > 0)
            {
                r.EndReminderSent = true;
                await db.SaveChangesAsync(ct);
                var left = (int)Math.Ceiling((r.WindowEnd - now).TotalMinutes);
                await notify.NotifyAsync(r.TenantId, r.AssigneeId, "Devriye bitmek üzere",
                    $"{r.RouteName}: {left} dk kaldı, {r.MissingCount} nokta eksik.", null, NotificationKinds.PatrolEndingSoon);
            }
        }
    }

    /// <summary>Aktif rotaların dünden yarına kadarki (bitmemiş) pencereleri için tur üretir. Var olanlar atlanır.</summary>
    private static async Task GenerateAsync(WfmDbContext db, DateTime now, CancellationToken ct)
    {
        var routes = await db.PatrolRoutes.IgnoreQueryFilters().Where(r => r.IsActive).ToListAsync(ct);
        if (routes.Count == 0) return;
        var today = PatrolSchedule.LocalToday(now);
        var horizon = now.AddDays(2);
        var ids = routes.Select(r => r.Id).ToList();
        var existing = (await db.PatrolRounds.IgnoreQueryFilters()
                .Where(r => ids.Contains(r.RouteId) && r.WindowEnd > now && r.WindowStart < horizon)
                .Select(r => new { r.RouteId, r.WindowStart }).ToListAsync(ct))
            .Select(x => (x.RouteId, x.WindowStart)).ToHashSet();

        var added = false;
        foreach (var route in routes.Where(r => r.Checkpoints.Count > 0))
            foreach (var day in new[] { today.AddDays(-1), today, today.AddDays(1) })
                foreach (var (start, end) in PatrolSchedule.WindowsOn(route, day))
                {
                    if (end <= now || !existing.Add((route.Id, start))) continue;
                    db.PatrolRounds.Add(NewRound(route, start, end));
                    added = true;
                }
        if (!added) return;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Aynı turu eşzamanlı başka bir çalıştırma üretti (benzersiz indeks); bir sonraki turda eksikler tamamlanır.
            db.ChangeTracker.Clear();
        }
    }

    public static PatrolRound NewRound(PatrolRoute route, DateTime start, DateTime end) => new()
    {
        TenantId = route.TenantId,
        RouteId = route.Id,
        RouteName = route.Name,
        AssigneeId = route.AssigneeId,
        WindowStart = start,
        WindowEnd = end,
        EnforceOrder = route.EnforceOrder,
        Checkpoints = route.Checkpoints.OrderBy(c => c.Order)
            .Select(c => new PatrolCheckpoint { Id = c.Id, Order = c.Order, Name = c.Name, Latitude = c.Latitude, Longitude = c.Longitude, RadiusMeters = c.RadiusMeters })
            .ToList()
    };

    private static async Task NotifyManagersAsync(WfmDbContext db, NotificationService notify, PatrolRound r, CancellationToken ct)
    {
        var worker = await db.Users.Where(u => u.Id == r.AssigneeId).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "Çalışan";
        var done = r.Checkpoints.Count - r.MissingCount;
        var title = r.Status == PatrolRoundStatus.Missed ? "Devriye yapılmadı" : "Devriye eksik";
        var body = $"{worker}, {r.RouteName} ({Local(r.WindowStart):HH:mm}–{Local(r.WindowEnd):HH:mm}): {done}/{r.Checkpoints.Count} nokta.";
        var managers = await (from u in db.Users
                              join ur in db.UserRoles on u.Id equals ur.UserId
                              join role in db.Roles on ur.RoleId equals role.Id
                              where u.TenantId == r.TenantId && u.IsActive && (role.Name == Roles.TenantAdmin || role.Name == Roles.Dispatcher)
                              select u.Id).Distinct().ToListAsync(ct);
        foreach (var m in managers)
            await notify.NotifyAsync(r.TenantId, m, title, body, null, NotificationKinds.PatrolMissed);
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, PatrolSchedule.Zone);
}

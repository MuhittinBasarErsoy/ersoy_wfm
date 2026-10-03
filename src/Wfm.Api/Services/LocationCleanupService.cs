using Microsoft.EntityFrameworkCore;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

/// <summary>Her kiracının saklama süresinden eski konum kayıtlarını günde bir siler.</summary>
public class LocationCleanupService(IServiceScopeFactory scopes, ILogger<LocationCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
                foreach (var t in await db.Tenants.Select(t => new { t.Id, t.LocationRetentionDays }).ToListAsync(ct))
                {
                    var cutoff = DateTime.UtcNow.AddDays(-t.LocationRetentionDays);
                    var n = await db.LocationPings.IgnoreQueryFilters()
                        .Where(p => p.TenantId == t.Id && p.RecordedAt < cutoff).ExecuteDeleteAsync(ct);
                    if (n > 0) log.LogInformation("{Count} eski konum kaydı silindi (kiracı {Tenant}).", n, t.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Konum temizleme başarısız.");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

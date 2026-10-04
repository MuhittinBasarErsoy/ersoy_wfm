using Wfm.Application.Contracts;
using Wfm.Domain.Enums;

namespace Wfm.Shared.UI.Services;

/// <summary>
/// Konum takibinden gelen her konumda "Yolda" olan görevlere uzaklığı kontrol eder; ~150 m içine girilince bir kez
/// "Vardım" önerisi üretir. Uygulama açıksa <see cref="Arrived"/> ile ekranda, arka plandaysa sistem bildirimiyle gösterilir.
/// </summary>
public sealed class ArrivalDetector(IFieldService field, INotificationPresenter presenter)
{
    public const double ArrivalMeters = 150;

    private readonly HashSet<Guid> _notified = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<WorkTaskDto, double>? Arrived;

    public async Task OnLocationAsync(double lat, double lng)
    {
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            var tasks = await field.GetTasksAsync();
            foreach (var t in tasks.Where(t => t.Status == WorkTaskStatus.EnRoute))
            {
                var d = Wfm.Domain.Entities.Geo.DistanceMeters(lat, lng, t.Latitude, t.Longitude);
                // Uzaklaşıp yeniden yaklaşınca tekrar önerilebilsin (ör. yanlış sokağa girildiyse).
                if (d > ArrivalMeters * 3) _notified.Remove(t.Id);
                if (d > ArrivalMeters || !_notified.Add(t.Id)) continue;
                Arrived?.Invoke(t, d);
                await presenter.ShowAsync("Görev konumuna ulaştınız", $"{t.Title}: \"Vardım\" ile durumu güncelleyin.", t.Id);
            }
        }
        catch { /* konum akışı hiçbir zaman bu yüzden durmamalı */ }
        finally { _lock.Release(); }
    }
}

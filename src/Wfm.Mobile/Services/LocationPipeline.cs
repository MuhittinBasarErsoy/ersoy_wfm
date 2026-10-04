using System.Text.Json;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Shared.UI.Services;

namespace Wfm.Mobile.Services;

/// <summary>
/// Cihazdan gelen konumları sunucuya iletir: bağlantı varsa SignalR ile anlık, yoksa SQLite kuyruğuna yazıp sonra toplu gönderir.
/// Platforma özel konum kaynakları (Android servisi, iOS CLLocationManager) buraya konum besler.
/// </summary>
public class LocationPipeline(WfmRealtime realtime, WfmApiClient api, LocalDb local, AuthSession session, ArrivalDetector arrival)
{
    private const int FlushThreshold = 20;
    private static readonly SemaphoreSlim FlushLock = new(1, 1);

    public static LocationPipeline? Current => IPlatformApplication.Current?.Services.GetService<LocationPipeline>();

    public async Task OnLocationAsync(double lat, double lng, double? accuracy, double? speed, double? heading)
    {
        if (session.Current is null) return;
        int? battery = null;
        try { battery = (int)Math.Round(Battery.Default.ChargeLevel * 100); } catch { /* desteklenmiyor */ }
        var ping = new LocationPingDto(lat, lng, accuracy, speed, heading, battery, DateTime.UtcNow);
        // Arka planda da çalışır: "Yolda" görevin adresine yaklaşınca yerel bildirimle "Vardım" önerilir.
        await arrival.OnLocationAsync(lat, lng);

        if (await realtime.TrySendLocationAsync(ping)) return;

        var db = await local.Db();
        await db.InsertAsync(new PendingPing { Json = JsonSerializer.Serialize(ping, WfmJson.Options) });
        if (await db.Table<PendingPing>().CountAsync() >= FlushThreshold) await FlushPendingAsync(local, api);
    }

    /// <summary>Kuyruktaki konumları 500'lük paketlerle REST üzerinden gönderir.</summary>
    public static async Task FlushPendingAsync(LocalDb local, WfmApiClient api)
    {
        if (!await FlushLock.WaitAsync(0)) return;
        try
        {
            var db = await local.Db();
            while (true)
            {
                var batch = await db.Table<PendingPing>().OrderBy(p => p.Id).Take(500).ToListAsync();
                if (batch.Count == 0) return;
                try
                {
                    await api.SendPingsAsync(batch.Select(p => JsonSerializer.Deserialize<LocationPingDto>(p.Json, WfmJson.Options)!).ToList());
                }
                catch (ApiException ex) when (ex.IsNetworkError || (int?)ex.Status >= 500)
                {
                    return; // sonra tekrar
                }
                catch (ApiException)
                {
                    // Kalıcı ret (ör. kullanıcı artık saha çalışanı değil): kuyruğu boşalt.
                }
                foreach (var p in batch) await db.DeleteAsync(p);
            }
        }
        finally
        {
            FlushLock.Release();
        }
    }
}

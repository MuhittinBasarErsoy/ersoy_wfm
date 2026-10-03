using Microsoft.JSInterop;
using Wfm.Application.Contracts;
using Wfm.Client;

namespace Wfm.Shared.UI.Services;

/// <summary>
/// Web'den (telefon tarayıcısı) çalışan saha personeli için konum takibi: sayfa açık olduğu sürece tarayıcı konumunu
/// en fazla 30 sn'de bir sunucuya gönderir. Arka plan takibi yalnızca mobil uygulamada mümkündür.
/// </summary>
public sealed class BrowserLocationTracker(WfmJs js, WfmApiClient api) : ILocationTracker, IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private DotNetObjectReference<BrowserLocationTracker>? _ref;
    private int _watchId = -1;
    private DateTime _lastSent = DateTime.MinValue;

    public bool IsSupported => true;
    public bool IsRunning => _watchId >= 0;

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;
        // İzin kontrolü: konum alınamıyorsa (izin yok) başlatma.
        if (await js.Invoke<GeoPoint?>("getPosition") is null) return false;
        _ref ??= DotNetObjectReference.Create(this);
        _watchId = await js.Invoke<int>("startWatch", _ref);
        return IsRunning;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        await js.Run("stopWatch", _watchId);
        _watchId = -1;
    }

    [JSInvokable]
    public async Task OnPosition(double lat, double lng, double? accuracy, double? speed, double? heading)
    {
        if (DateTime.UtcNow - _lastSent < MinInterval) return;
        _lastSent = DateTime.UtcNow;
        try
        {
            await api.SendPingsAsync([new LocationPingDto(lat, lng, accuracy, speed, heading, null, DateTime.UtcNow)]);
        }
        catch (ApiException) { /* bir sonraki konumda tekrar denenir */ }
    }

    public void Dispose() => _ref?.Dispose();
}

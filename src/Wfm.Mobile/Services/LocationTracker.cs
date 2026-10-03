using Wfm.Shared.UI.Services;

namespace Wfm.Mobile.Services;

/// <summary>
/// Mesai süresince arka planda konum takibi.
/// Android: kalıcı bildirimli Foreground Service. iOS: CLLocationManager arka plan konum modu.
/// Windows (geliştirme): uygulama açıkken periyodik konum.
/// </summary>
public class LocationTracker : ILocationTracker
{
    private const string RunningKey = "tracking_running";

    public bool IsSupported => true;
    public bool IsRunning { get; private set; }

#if WINDOWS
    private PeriodicTimer? _timer;
#elif IOS
    private IosPlatform.IosLocationManager? _ios;
#endif

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;
        if (!await RequestPermissionsAsync()) return false;

        await MainThread.InvokeOnMainThreadAsync(StartPlatform);
        IsRunning = true;
        Preferences.Default.Set(RunningKey, true);
        return true;
    }

    public async Task StopAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(StopPlatform);
        IsRunning = false;
        Preferences.Default.Set(RunningKey, false);
    }

    private static async Task<bool> RequestPermissionsAsync()
    {
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var whenInUse = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (whenInUse != PermissionStatus.Granted) return false;
#if ANDROID || IOS
            // Arka plan ("Her zaman") izni: verilmezse takip yalnızca uygulama/servis aktifken çalışır.
            if (await Permissions.CheckStatusAsync<Permissions.LocationAlways>() != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.LocationAlways>();
#endif
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                await Permissions.RequestAsync<Permissions.PostNotifications>();
#endif
            return true;
        });
    }

    private void StartPlatform()
    {
#if ANDROID
        var ctx = global::Android.App.Application.Context;
        var intent = new global::Android.Content.Intent(ctx, typeof(Droid.LocationForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(intent);
        else ctx.StartService(intent);
#elif IOS
        _ios ??= new IosPlatform.IosLocationManager();
        _ios.Start();
#elif WINDOWS
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var timer = _timer;
        _ = Task.Run(async () =>
        {
            do
            {
                try
                {
                    var loc = await MainThread.InvokeOnMainThreadAsync(() =>
                        Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10))));
                    if (loc is not null && LocationPipeline.Current is { } p)
                        await p.OnLocationAsync(loc.Latitude, loc.Longitude, loc.Accuracy, loc.Speed, loc.Course);
                }
                catch { /* konum alınamadı, sonraki turda tekrar */ }
            } while (await timer.WaitForNextTickAsync());
        });
#endif
    }

    private void StopPlatform()
    {
#if ANDROID
        var ctx = global::Android.App.Application.Context;
        ctx.StopService(new global::Android.Content.Intent(ctx, typeof(Droid.LocationForegroundService)));
#elif IOS
        _ios?.Stop();
#elif WINDOWS
        _timer?.Dispose();
        _timer = null;
#endif
    }
}

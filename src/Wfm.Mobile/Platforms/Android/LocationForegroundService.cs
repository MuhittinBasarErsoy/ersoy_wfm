using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using AndroidX.Core.App;
using Wfm.Mobile.Services;

namespace Wfm.Mobile.Droid;

/// <summary>
/// Mesai süresince konum toplayan ön plan servisi. Kalıcı bir bildirim gösterir; Android uygulama arka plandayken
/// veya ekran kapalıyken de konum almaya devam eder.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public class LocationForegroundService : Service, ILocationListener
{
    private const int NotificationId = 4201;
    private const string ChannelId = "wfm_tracking";
    private const long MinTimeMs = 30_000;   // en sık 30 sn'de bir
    private const float MinDistanceM = 20;   // veya 20 m hareket

    private LocationManager? _locationManager;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();
        var openApp = PendingIntent.GetActivity(this, 0,
            PackageManager?.GetLaunchIntentForPackage(PackageName ?? "") ?? new Intent(this, typeof(MainActivity))!,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Mesaidesiniz")
            .SetContentText("Konumunuz yöneticinizle paylaşılıyor.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogMap)
            .SetOngoing(true)
            .SetContentIntent(openApp)
            .SetPriority(NotificationCompat.PriorityLow)
            .Build()!;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
        else
            StartForeground(NotificationId, notification);

        StartLocationUpdates();
        return StartCommandResult.Sticky;
    }

    private void StartLocationUpdates()
    {
        _locationManager ??= (LocationManager?)GetSystemService(LocationService);
        if (_locationManager is null) return;
        try
        {
            if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
                _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, MinTimeMs, MinDistanceM, this, Looper.MainLooper);
            if (_locationManager.IsProviderEnabled(LocationManager.NetworkProvider))
                _locationManager.RequestLocationUpdates(LocationManager.NetworkProvider, MinTimeMs, MinDistanceM, this, Looper.MainLooper);
        }
        catch (Java.Lang.SecurityException)
        {
            // İzin geri alınmış: servisi durdur.
            StopSelf();
        }
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is not null) return;
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Konum takibi", NotificationImportance.Low)
        {
            Description = "Mesai süresince konum paylaşımı"
        });
    }

    public void OnLocationChanged(global::Android.Locations.Location location)
    {
        var pipeline = LocationPipeline.Current;
        if (pipeline is null) return;
        _ = pipeline.OnLocationAsync(location.Latitude, location.Longitude,
            location.HasAccuracy ? location.Accuracy : null,
            location.HasSpeed ? location.Speed : null,
            location.HasBearing ? location.Bearing : null);
    }

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
    public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }

    public override void OnDestroy()
    {
        _locationManager?.RemoveUpdates(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}

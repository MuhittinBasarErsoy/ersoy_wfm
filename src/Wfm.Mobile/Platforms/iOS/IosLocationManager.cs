using CoreLocation;
using Wfm.Mobile.Services;

namespace Wfm.Mobile.IosPlatform;

/// <summary>
/// iOS arka plan konum takibi. Info.plist'te UIBackgroundModes=location ve konum izin açıklamaları gerekir.
/// Uygulama askıya alınsa bile "significant location change" ile sistem uygulamayı uyandırır.
/// </summary>
public class IosLocationManager : CLLocationManagerDelegate
{
    private readonly CLLocationManager _manager = new();

    public IosLocationManager()
    {
        _manager.Delegate = this;
        _manager.DesiredAccuracy = CLLocation.AccuracyNearestTenMeters;
        _manager.DistanceFilter = 20;
        _manager.PausesLocationUpdatesAutomatically = false;
        _manager.ActivityType = CLActivityType.AutomotiveNavigation;
    }

    public void Start()
    {
        _manager.RequestAlwaysAuthorization();
        _manager.AllowsBackgroundLocationUpdates = true;
        _manager.ShowsBackgroundLocationIndicator = true;
        _manager.StartUpdatingLocation();
        _manager.StartMonitoringSignificantLocationChanges();
    }

    public void Stop()
    {
        _manager.StopUpdatingLocation();
        _manager.StopMonitoringSignificantLocationChanges();
        _manager.AllowsBackgroundLocationUpdates = false;
    }

    public override void LocationsUpdated(CLLocationManager manager, CLLocation[] locations)
    {
        var l = locations.LastOrDefault();
        if (l is null || LocationPipeline.Current is not { } pipeline) return;
        _ = pipeline.OnLocationAsync(l.Coordinate.Latitude, l.Coordinate.Longitude,
            l.HorizontalAccuracy >= 0 ? l.HorizontalAccuracy : null,
            l.Speed >= 0 ? l.Speed : null,
            l.Course >= 0 ? l.Course : null);
    }
}

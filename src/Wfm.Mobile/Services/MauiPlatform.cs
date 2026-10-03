using System.Text.Json;
using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Shared.UI.Services;

namespace Wfm.Mobile.Services;

/// <summary>MAUI native yetenekleri: GPS, kamera, harita/telefon uygulamaları, yerel bildirim.</summary>
public class MauiPlatform : IGeolocationService, ICameraService, IExternalActions, INotificationPresenter, IPlatformInfo
{
    private const int MaxPhotoSize = 1600;

    public bool IsMobile => true;

    // ---------- Konum ----------
    public async Task<GeoPoint?> GetCurrentAsync()
    {
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted) return null;
                var loc = await Geolocation.Default.GetLastKnownLocationAsync();
                if (loc is null || DateTimeOffset.UtcNow - loc.Timestamp > TimeSpan.FromMinutes(2))
                    loc = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(10)));
                return loc is null ? null : new GeoPoint(loc.Latitude, loc.Longitude, loc.Accuracy);
            });
        }
        catch
        {
            return null; // konum servisi kapalı vb.
        }
    }

    // ---------- Kamera ----------
    public bool IsSupported => MediaPicker.Default.IsCaptureSupported;

    public async Task<CapturedFile?> CapturePhotoAsync()
    {
        var photo = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
                throw new InvalidOperationException("Kamera izni verilmedi.");
            return await MediaPicker.Default.CapturePhotoAsync();
        });
        if (photo is null) return null;

        await using var stream = await photo.OpenReadAsync();
        var bytes = await Downscale(stream);
        return new CapturedFile(bytes, $"foto_{DateTime.Now:yyyyMMdd_HHmmss}.jpg", "image/jpeg");
    }

    /// <summary>Fotoğrafı en fazla 1600 px'e küçültür ve JPEG'e çevirir (mobil veri ve depolama tasarrufu).</summary>
    private static async Task<byte[]> Downscale(Stream input)
    {
#if ANDROID || IOS
        var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(input);
        if (image.Width > MaxPhotoSize || image.Height > MaxPhotoSize)
            image = image.Downsize(MaxPhotoSize, disposeOriginal: true);
        using var ms = new MemoryStream();
        await image.SaveAsync(ms, Microsoft.Maui.Graphics.ImageFormat.Jpeg, 0.8f);
        return ms.ToArray();
#else
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms);
        return ms.ToArray();
#endif
    }

    // ---------- Harici uygulamalar ----------
    public Task OpenDirectionsAsync(double lat, double lng, string label) =>
        MainThread.InvokeOnMainThreadAsync(() => Map.Default.OpenAsync(lat, lng,
            new MapLaunchOptions { Name = label, NavigationMode = NavigationMode.Driving }));

    public Task CallAsync(string phone) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (PhoneDialer.Default.IsSupported) PhoneDialer.Default.Open(phone);
    });

    // ---------- Bildirim ----------
    public async Task ShowAsync(string title, string body, Guid? taskId)
    {
        // Uygulama açıkken UI içi snackbar yeterli; arka plandayken sistem bildirimi göster.
        if (!AppState.IsInBackground) return;
        try
        {
            await LocalNotificationCenter.Current.Show(new NotificationRequest
            {
                NotificationId = Math.Abs((taskId ?? Guid.NewGuid()).GetHashCode() % 100000),
                Title = title,
                Description = body,
                ReturningData = taskId?.ToString() ?? ""
            });
        }
        catch { /* bildirim izni yok */ }
    }
}

/// <summary>Oturumu cihazın güvenli deposunda (Keychain / Keystore) saklar.</summary>
public class SecureTokenStore : ITokenStore
{
    private const string Key = "wfm.auth";

    public async Task<AuthResponse?> GetAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<AuthResponse>(json, WfmJson.Options);
        }
        catch
        {
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public async Task SetAsync(AuthResponse? auth)
    {
        if (auth is null) SecureStorage.Default.Remove(Key);
        else await SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(auth, WfmJson.Options));
    }
}

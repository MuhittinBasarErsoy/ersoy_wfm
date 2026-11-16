using System.Text.Json;
using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Shared.UI.Services;

namespace Wfm.Mobile.Services;

/// <summary>MAUI native yetenekleri: GPS, kamera, harita/telefon uygulamaları, yerel bildirim.</summary>
public class MauiPlatform(WfmJs js) : IGeolocationService, ICameraService, IExternalActions, INotificationPresenter, IPlatformInfo
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

    public async Task<CapturedFile?> CapturePhotoAsync(bool includeLocation = false)
    {
        var photo = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
                throw new InvalidOperationException("Kamera izni verilmedi.");
            return await MediaPicker.Default.CapturePhotoAsync();
        });
        if (photo is null) return null;

        var capturedAt = DateTimeOffset.Now;
        await using var stream = await photo.OpenReadAsync();
        var bytes = await Downscale(stream);
        var fileName = $"foto_{capturedAt:yyyyMMdd_HHmmss}.jpg";
        if (!includeLocation) return new CapturedFile(bytes, fileName, "image/jpeg");

        var stamp = await ReadPhotoLocationAsync(capturedAt.UtcDateTime);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new[]
        {
            "Blue WFM · Çekim konumu",
            stamp.Address ?? "Adres çözümlenemedi; çekim koordinatları aşağıdadır.",
            $"Enlem {stamp.Latitude.ToString("F6", culture)} · Boylam {stamp.Longitude.ToString("F6", culture)}",
            $"{capturedAt:dd.MM.yyyy HH:mm:ss} (UTC{capturedAt:zzz})"
        };
        try
        {
            var stamped = await js.Invoke<string>("stampLocationPhoto", Convert.ToBase64String(bytes), lines);
            return new CapturedFile(Convert.FromBase64String(stamped), fileName, "image/jpeg", stamp);
        }
        catch (Exception ex) when (ex is Microsoft.JSInterop.JSException or FormatException)
        {
            throw new InvalidOperationException("Fotoğrafa konum damgası eklenemedi. Tekrar deneyin.", ex);
        }
    }

    private static async Task<PhotoLocation> ReadPhotoLocationAsync(DateTime capturedAt)
    {
        Location location;
        try
        {
            location = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
                    throw new InvalidOperationException("Konumlu fotoğraf için konum izni gerekli.");
                return await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(15)))
                    ?? throw new InvalidOperationException("Çekim konumu alınamadı. Konum servislerini açıp tekrar deneyin.");
            });
        }
        catch (Exception ex) when (ex is FeatureNotEnabledException or FeatureNotSupportedException or PermissionException)
        {
            throw new InvalidOperationException("Konumlu fotoğraf için konum servislerini ve konum iznini açın.", ex);
        }
        if (!double.IsFinite(location.Latitude) || !double.IsFinite(location.Longitude) ||
            Math.Abs(location.Latitude) > 90 || Math.Abs(location.Longitude) > 180 ||
            DateTimeOffset.UtcNow - location.Timestamp > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Güncel çekim konumu alınamadı. Tekrar deneyin.");

        string? address = null;
        try
        {
            var placemark = (await Geocoding.Default.GetPlacemarksAsync(location.Latitude, location.Longitude)
                .WaitAsync(TimeSpan.FromSeconds(8))).FirstOrDefault();
            if (placemark is not null)
            {
                var parts = new[] { placemark.Thoroughfare, placemark.SubLocality, placemark.Locality, placemark.AdminArea, placemark.CountryName };
                var text = string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
                if (!string.IsNullOrWhiteSpace(text)) address = text[..Math.Min(text.Length, 500)];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Çevrimdışıyken adres çözümlenemese de gerçek GPS koordinatları fotoğrafta kalır.
            System.Diagnostics.Debug.WriteLine($"Fotoğraf adresi alınamadı: {ex.GetType().Name}");
        }
        return new PhotoLocation(location.Latitude, location.Longitude, location.Accuracy, capturedAt, address);
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
    public Task OpenDirectionsAsync(double lat, double lng, string label, bool walking = false) =>
        MainThread.InvokeOnMainThreadAsync(() => Map.Default.OpenAsync(lat, lng,
            new MapLaunchOptions { Name = label, NavigationMode = walking ? NavigationMode.Walking : NavigationMode.Driving }));

    public Task CallAsync(string phone) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (PhoneDialer.Default.IsSupported) PhoneDialer.Default.Open(phone);
    });

    // ---------- Bildirim ----------
    public async Task ShowAsync(string title, string body, Guid? taskId)
    {
        // Uygulama açıkken UI içi snackbar yeterli; arka plandayken sistem bildirimi göster.
        if (!AppState.IsInBackground) return;
        // iPhone'a aynı bildirim sunucudan push olarak da geliyor; ikinci kez gösterme.
        if (PushRegistration.ServerPushActive) return;
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

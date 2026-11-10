using Plugin.LocalNotification;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Shared.UI.Services;

namespace Wfm.Mobile.Services;

/// <summary>
/// Girişte bildirim iznini ister (iOS ve Android 13+ izinsiz bildirim göstermez) ve iPhone'u push (APNs) için kaydeder.
/// Apple cihaz anahtarını AppDelegate üzerinden verir; oturum açıkken sunucuya iletilir, çıkışta silinir.
/// </summary>
public sealed class PushRegistration(AuthSession session, WfmApiClient api) : IPushRegistrar
{
    private static PushRegistration? _instance;
    private static string? _iosToken;
    private Guid? _registeredUser;

    /// <summary>Sunucu push gönderiyor; telefon aynı bildirimi yerel olarak ikinci kez göstermez.</summary>
    public static bool ServerPushActive { get; private set; }

    public void Start()
    {
        _instance = this;
        session.Changed += auth => _ = OnSessionChangedAsync(auth);
        _ = OnSessionChangedAsync(session.Current);
    }

    /// <summary>AppDelegate, Apple'dan cihaz anahtarı gelince çağırır.</summary>
    public static void OnIosToken(string token)
    {
        _iosToken = token;
        if (_instance is { } push)
        {
            push._registeredUser = null;
            _ = push.RegisterAsync();
        }
    }

    private async Task OnSessionChangedAsync(AuthResponse? auth)
    {
        if (auth is null)
        {
            _registeredUser = null;
            ServerPushActive = false;
            return;
        }
        // Oturum yenilendiğinde de tetiklenir; aynı kullanıcı için izin ve kayıt bir kez yeter.
        if (_registeredUser == auth.User.Id) return;
        await RequestPermissionAsync();
        await RegisterAsync();
    }

    private static async Task RequestPermissionAsync()
    {
        try
        {
            if (!await LocalNotificationCenter.Current.AreNotificationsEnabled())
                await LocalNotificationCenter.Current.RequestNotificationPermission();
        }
        catch { /* izin penceresi gösterilemedi; uygulama içi bildirimler yine çalışır */ }
#if IOS
        MainThread.BeginInvokeOnMainThread(() => UIKit.UIApplication.SharedApplication.RegisterForRemoteNotifications());
#endif
    }

    private async Task RegisterAsync()
    {
        if (_iosToken is not { } token || session.Current is not { } auth) return;
        try
        {
            ServerPushActive = (await api.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, token))).PushEnabled;
            _registeredUser = auth.User.Id;
        }
        catch (ApiException) { /* çevrimdışı: bir sonraki oturum değişiminde ya da yeni anahtarda tekrar denenir */ }
    }

    public async Task UnregisterAsync()
    {
        ServerPushActive = false;
        _registeredUser = null;
        if (_iosToken is not { } token || session.Current is null) return;
        try { await api.UnregisterDeviceAsync(token); }
        catch (ApiException) { /* çevrimdışı: sunucu, telefon başka kullanıcıyla açılınca kaydı ona devreder */ }
    }
}

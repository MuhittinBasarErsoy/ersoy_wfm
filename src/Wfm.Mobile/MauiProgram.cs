using Microsoft.Extensions.Logging;
using Plugin.LocalNotification;
using Wfm.Client;
using Wfm.Mobile.Services;
using Wfm.Shared.UI.Services;

namespace Wfm.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseLocalNotification()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();

        // Ortak istemci + UI (mobilde tek kullanıcı, tek oturum → singleton).
        builder.Services.AddWfmClient(ApiBaseUrl(), singletonSession: true);
        builder.Services.AddWfmUi(singleton: true);
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();

        // Offline depo ve saha servisi
        builder.Services.AddSingleton<LocalDb>();
        builder.Services.AddSingleton<OfflineFieldService>();
        builder.Services.AddSingleton<IFieldService>(sp => sp.GetRequiredService<OfflineFieldService>());

        // Native yetenekler
        builder.Services.AddSingleton<MauiPlatform>();
        builder.Services.AddSingleton<IPlatformInfo>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<IGeolocationService>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<ICameraService>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<IExternalActions>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<INotificationPresenter>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<ILocationTracker, LocationTracker>();
        builder.Services.AddSingleton<LocationPipeline>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    /// <summary>
    /// API adresi. Geliştirmede Android emülatörü bilgisayara 10.0.2.2 üzerinden erişir.
    /// Gerçek cihaz/üretim için "api_base_url" tercihini ayarlayın veya burayı değiştirin.
    /// </summary>
    private static string ApiBaseUrl()
    {
        var configured = Preferences.Default.Get("api_base_url", "");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
#if ANDROID
        return "http://10.0.2.2:5211";
#else
        return "http://localhost:5211";
#endif
    }
}

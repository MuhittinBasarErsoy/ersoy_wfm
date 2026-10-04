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

    /// <summary>Canlı sunucu (Tailscale Funnel, bkz. deploy/README.md).</summary>
    private const string ProductionApiUrl = "https://wfm-api.tailc23b56.ts.net";

    /// <summary>
    /// API adresi. "api_base_url" tercihi her zaman önceliklidir. Release derlemeleri canlı sunucuya,
    /// Debug derlemeleri yerel API'ye bağlanır (Android emülatörü bilgisayara 10.0.2.2 üzerinden erişir).
    /// </summary>
    private static string ApiBaseUrl()
    {
        var configured = Preferences.Default.Get("api_base_url", "");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
#if !DEBUG
        return ProductionApiUrl;
#elif ANDROID
        return "http://10.0.2.2:5211";
#else
        return "http://localhost:5211";
#endif
    }
}

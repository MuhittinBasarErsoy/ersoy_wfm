using Microsoft.JSInterop;

namespace Wfm.Shared.UI.Services;

/// <summary>wfm.js ES modülüne tip güvenli erişim.</summary>
public sealed class WfmJs(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;

    private Task<IJSObjectReference> Module =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/Wfm.Shared.UI/wfm.js").AsTask();

    public async ValueTask<T> Invoke<T>(string fn, params object?[] args) => await (await Module).InvokeAsync<T>(fn, args);

    public async ValueTask Run(string fn, params object?[] args)
    {
        try
        {
            await (await Module).InvokeVoidAsync(fn, args);
        }
        catch (JSDisconnectedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try { await _module.Result.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
    }
}

/// <summary>Web (tarayıcı) varsayılan platform implementasyonları. Mobil uygulama bunları native olanlarla değiştirir.</summary>
public class BrowserPlatform(WfmJs js) : IGeolocationService, ICameraService, ILocationTracker, IExternalActions, INotificationPresenter
{
    public async Task<GeoPoint?> GetCurrentAsync()
    {
        try { return await js.Invoke<GeoPoint?>("getPosition"); }
        catch { return null; }
    }

    public bool IsSupported => false;
    public Task<CapturedFile?> CapturePhotoAsync() => Task.FromResult<CapturedFile?>(null);

    bool ILocationTracker.IsSupported => false;
    public bool IsRunning => false;
    public Task<bool> StartAsync() => Task.FromResult(false);
    public Task StopAsync() => Task.CompletedTask;

    public Task OpenDirectionsAsync(double lat, double lng, string label) =>
        js.Run("openUrl", $"https://www.google.com/maps/dir/?api=1&destination={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}").AsTask();

    public Task CallAsync(string phone) => js.Run("openUrl", $"tel:{phone}").AsTask();

    public Task ShowAsync(string title, string body, Guid? taskId) => Task.CompletedTask;
}

public class WebPlatformInfo : IPlatformInfo
{
    public bool IsMobile => false;
}

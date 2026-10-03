using Wfm.Mobile.Services;

namespace Wfm.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly OfflineFieldService _field;

    public App(OfflineFieldService field)
    {
        InitializeComponent();
        _field = field;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "WFM Saha" };
        // Uygulama ön plana geldiğinde bekleyen işlemleri gönder ve görevleri tazele.
        window.Resumed += (_, _) => { AppState.IsInBackground = false; _ = _field.SyncAsync(); };
        window.Activated += (_, _) => AppState.IsInBackground = false;
        window.Stopped += (_, _) => AppState.IsInBackground = true;
        return window;
    }
}

public static class AppState
{
    /// <summary>Uygulama arka plandayken yeni bildirimler sistem bildirimi olarak gösterilir.</summary>
    public static volatile bool IsInBackground;
}

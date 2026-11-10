using Wfm.Mobile.Services;

namespace Wfm.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly OfflineFieldService _field;

    public App(OfflineFieldService field, PushRegistration push)
    {
        InitializeComponent();
        _field = field;
        push.Start(); // girişte bildirim izni ister, iPhone'u push için kaydeder
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

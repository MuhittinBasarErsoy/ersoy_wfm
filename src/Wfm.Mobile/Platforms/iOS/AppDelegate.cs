using Foundation;
using UIKit;
using Wfm.Mobile.Services;

namespace Wfm.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// MauiUIApplicationDelegate bu isteğe bağlı protokol yöntemlerini tanımlamaz; seçiciyle dışa açılır.
	// APNs cihaz anahtarı: PushRegistration oturum açıkken sunucuya bildirir.
	[Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
	public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken) =>
		PushRegistration.OnIosToken(Convert.ToHexString(deviceToken.ToArray()).ToLowerInvariant());

	[Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
	public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error) =>
		System.Diagnostics.Debug.WriteLine($"APNs kaydı başarısız: {error.LocalizedDescription}");
}

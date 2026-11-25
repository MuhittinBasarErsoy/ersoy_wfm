using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain;

namespace Wfm.Web;

/// <summary>Denetçi oturumu yalnızca açık bağlantıda tutulur; diğer oturumlar şifreli tarayıcı deposunda saklanır.</summary>
public class BrowserTokenStore(ProtectedLocalStorage storage) : ITokenStore
{
    private const string Key = "wfm.auth";
    private AuthResponse? _observerAuth;

    public async Task<AuthResponse?> GetAsync()
    {
        if (_observerAuth is not null) return _observerAuth;
        try
        {
            var result = await storage.GetAsync<AuthResponse>(Key);
            if (result.Success && result.Value?.User.Roles.Contains(Roles.VehicleObserver) == true)
            {
                await storage.DeleteAsync(Key);
                return null;
            }
            return result.Success ? result.Value : null;
        }
        catch
        {
            // Bozuk/eski anahtarla şifrelenmiş değer: oturumu sıfırla.
            return null;
        }
    }

    public async Task SetAsync(AuthResponse? auth)
    {
        _observerAuth = auth?.User.Roles.Contains(Roles.VehicleObserver) == true ? auth : null;
        if (auth is null || _observerAuth is not null) await storage.DeleteAsync(Key);
        else await storage.SetAsync(Key, auth);
    }
}

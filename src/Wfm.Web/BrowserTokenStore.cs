using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Wfm.Application.Contracts;
using Wfm.Client;

namespace Wfm.Web;

/// <summary>Oturumu tarayıcının localStorage'ında, ASP.NET Data Protection ile şifrelenmiş olarak saklar.</summary>
public class BrowserTokenStore(ProtectedLocalStorage storage) : ITokenStore
{
    private const string Key = "wfm.auth";

    public async Task<AuthResponse?> GetAsync()
    {
        try
        {
            var result = await storage.GetAsync<AuthResponse>(Key);
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
        if (auth is null) await storage.DeleteAsync(Key);
        else await storage.SetAsync(Key, auth);
    }
}

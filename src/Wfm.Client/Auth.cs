using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Wfm.Application.Contracts;

namespace Wfm.Client;

public class WfmClientOptions
{
    /// <summary>API kök adresi, ör. http://localhost:5211 (Android emülatöründe http://10.0.2.2:5211).</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5211";
}

/// <summary>Oturum bilgisinin platforma göre saklandığı yer (web: tarayıcı depolama, mobil: SecureStorage).</summary>
public interface ITokenStore
{
    Task<AuthResponse?> GetAsync();
    Task SetAsync(AuthResponse? auth);
}

/// <summary>Oturumu yönetir: giriş, çıkış, token yenileme. Oturum değişince <see cref="Changed"/> tetiklenir.</summary>
public class AuthSession(ITokenStore store, WfmClientOptions options)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AuthResponse? _current;
    private bool _loaded;

    public event Action<AuthResponse?>? Changed;

    public async Task<AuthResponse?> GetAsync()
    {
        if (!_loaded)
        {
            _current = await store.GetAsync();
            _loaded = true;
        }
        return _current;
    }

    public async Task SetAsync(AuthResponse? auth)
    {
        _current = auth;
        _loaded = true;
        await store.SetAsync(auth);
        Changed?.Invoke(auth);
    }

    /// <summary>Geçerli bir access token döner; süresi dolmak üzereyse yeniler.</summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        var auth = await GetAsync();
        if (auth is null) return null;
        if (!forceRefresh && auth.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return auth.AccessToken;

        await _refreshLock.WaitAsync();
        try
        {
            // Başka bir çağrı biz beklerken yenilemiş olabilir.
            if (_current is { } cur && cur.AccessToken != auth.AccessToken && cur.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
                return cur.AccessToken;

            using var http = new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl) };
            HttpResponseMessage res;
            try
            {
                res = await http.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken), WfmJson.Options);
            }
            catch (HttpRequestException)
            {
                // Çevrimdışı: eldeki token'ı dön, sunucu reddederse çağıran tarafta ele alınır.
                return auth.AccessToken;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                await SetAsync(null);
                return null;
            }
            res.EnsureSuccessStatusCode();
            var fresh = await res.Content.ReadFromJsonAsync<AuthResponse>(WfmJson.Options);
            await SetAsync(fresh);
            return fresh?.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}

/// <summary>Her isteğe Bearer token ekler; 401 alınırsa bir kez yenileyip tekrar dener.</summary>
public class AuthHeaderHandler(AuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (await session.GetAccessTokenAsync() is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || request.Headers.Authorization is null) return response;

        // İçerik akışı tekrar okunabiliyorsa yeniden dene (multipart yüklemeler hariç).
        if (request.Content is StreamContent or MultipartContent) return response;
        if (await session.GetAccessTokenAsync(forceRefresh: true) is not { } fresh) return response;

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(request, ct);
    }
}

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wfm.Application.Contracts;

namespace Wfm.Client;

/// <summary>
/// SignalR bağlantıları: canlı konum (TrackingHub) ve bildirim/görev değişiklikleri (NotificationHub).
/// Otomatik yeniden bağlanır; bağlantı koptuğunda <see cref="StateChanged"/> ile UI bilgilendirilir.
/// </summary>
public sealed class WfmRealtime(AuthSession session, WfmClientOptions options) : IAsyncDisposable
{
    private HubConnection? _tracking;
    private HubConnection? _notifications;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<WorkerLocationDto>? LocationUpdated;
    public event Action<WorkTaskDto>? TaskChanged;
    public event Action<NotificationDto>? NotificationReceived;
    public event Action<TaskCommentDto>? CommentAdded;
    public event Action<bool>? StateChanged;

    public bool IsConnected => _notifications?.State == HubConnectionState.Connected;

    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _tracking ??= Build(HubPaths.Tracking, c =>
                c.On<WorkerLocationDto>(HubMethods.LocationUpdated, l => LocationUpdated?.Invoke(l)));
            _notifications ??= Build(HubPaths.Notifications, c =>
            {
                c.On<WorkTaskDto>(HubMethods.TaskChanged, t => TaskChanged?.Invoke(t));
                c.On<NotificationDto>(HubMethods.NotificationReceived, n => NotificationReceived?.Invoke(n));
                c.On<TaskCommentDto>(HubMethods.CommentAdded, m => CommentAdded?.Invoke(m));
            });
            await StartIfNeeded(_tracking);
            await StartIfNeeded(_notifications);
            StateChanged?.Invoke(IsConnected);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Saha çalışanı canlı konum gönderir. Bağlantı yoksa false döner (çağıran kuyruğa alır).</summary>
    public async Task<bool> TrySendLocationAsync(LocationPingDto ping)
    {
        if (_tracking?.State != HubConnectionState.Connected) return false;
        try
        {
            await _tracking.InvokeAsync(HubMethods.SendLocation, ping);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_tracking is not null) await _tracking.DisposeAsync();
            if (_notifications is not null) await _notifications.DisposeAsync();
            _tracking = _notifications = null;
            StateChanged?.Invoke(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private HubConnection Build(string path, Action<HubConnection> register)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(options.ApiBaseUrl.TrimEnd('/') + path, o => o.AccessTokenProvider = () => session.GetAccessTokenAsync())
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        register(conn);
        conn.Reconnecting += _ => { StateChanged?.Invoke(false); return Task.CompletedTask; };
        conn.Reconnected += _ => { StateChanged?.Invoke(true); return Task.CompletedTask; };
        conn.Closed += _ => { StateChanged?.Invoke(false); return Task.CompletedTask; };
        return conn;
    }

    /// <summary>Son bağlantı hatası (tanı amaçlı).</summary>
    public string? LastError { get; private set; }

    private async Task StartIfNeeded(HubConnection c)
    {
        if (c.State != HubConnectionState.Disconnected) return;
        try
        {
            await c.StartAsync();
            LastError = null;
        }
        catch (Exception ex)
        {
            // Çevrimdışı; bir sonraki StartAsync çağrısında tekrar denenir.
            LastError = ex.Message;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(ctx.PreviousRetryCount, 5))));
    }
}

public static class ClientServiceCollectionExtensions
{
    /// <summary>API istemcisi, oturum ve SignalR servislerini kaydeder. <see cref="ITokenStore"/> platform tarafından kaydedilmeli.</summary>
    public static IServiceCollection AddWfmClient(this IServiceCollection services, string apiBaseUrl, bool singletonSession, string? publicBaseUrl = null)
    {
        var options = new WfmClientOptions { ApiBaseUrl = apiBaseUrl, PublicBaseUrl = publicBaseUrl };
        services.AddSingleton(options);
        if (singletonSession)
        {
            // Mobil: tek kullanıcı, uygulama boyu tek oturum.
            services.AddSingleton<AuthSession>();
            services.AddSingleton<WfmRealtime>();
            services.AddSingleton(sp => new WfmApiClient(
                new HttpClient(new AuthHeaderHandler(sp.GetRequiredService<AuthSession>()) { InnerHandler = new HttpClientHandler() })
                {
                    BaseAddress = new Uri(apiBaseUrl), Timeout = TimeSpan.FromSeconds(30)
                }, options));
        }
        else
        {
            // Web (Blazor Server): her tarayıcı bağlantısının (circuit) kendi oturumu.
            services.AddScoped<AuthSession>();
            services.AddScoped<WfmRealtime>();
            services.AddScoped(sp => new WfmApiClient(
                new HttpClient(new AuthHeaderHandler(sp.GetRequiredService<AuthSession>()) { InnerHandler = new HttpClientHandler() })
                {
                    BaseAddress = new Uri(apiBaseUrl), Timeout = TimeSpan.FromSeconds(30)
                }, options));
        }
        return services;
    }
}

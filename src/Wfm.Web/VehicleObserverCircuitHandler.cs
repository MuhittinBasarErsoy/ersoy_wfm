using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain;

namespace Wfm.Web;

public sealed class VehicleObserverCircuitHandler(AuthSession session, WfmClientOptions options,
    ILogger<VehicleObserverCircuitHandler> log) : CircuitHandler
{
    private int _closing;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => CloseAsync();
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) => CloseAsync();

    private async Task CloseAsync()
    {
        var auth = session.Current;
        if (auth?.User.Roles.Contains(Roles.VehicleObserver) != true || Interlocked.Exchange(ref _closing, 1) != 0) return;
        try
        {
            // No token refresh or browser storage is needed after the connection has gone away.
            using var http = new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(auth.RefreshToken, ConnectionClosed: true));
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Interlocked.Exchange(ref _closing, 0);
            log.LogWarning("Denetçi bağlantısı kapanırken API'ye ulaşılamadı; oturum süre sınırına tabidir.");
        }
    }
}

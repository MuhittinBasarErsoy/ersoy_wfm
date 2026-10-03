using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Domain;

namespace Wfm.Api.Hubs;

public static class HubGroups
{
    /// <summary>Bir kiracının konum/görev izleme yetkisi olan kullanıcıları (dispeçer, yönetici, izleyici).</summary>
    public static string Watchers(Guid tenantId) => $"tenant:{tenantId}:watchers";
    public static string User(Guid userId) => $"user:{userId}";
}

/// <summary>Canlı konum: saha çalışanları konum gönderir, izleyiciler anlık alır.</summary>
[Authorize]
public class TrackingHub(TrackingService tracking) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        if (user.HasPolicy(Policies.ViewTracking))
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Watchers(user.TenantId()));
        await base.OnConnectedAsync();
    }

    [Authorize(Policy = Policies.FieldWork)]
    public Task SendLocation(LocationPingDto ping) =>
        tracking.RecordAsync(Context.User!.TenantId(), Context.User!.UserId(), [ping]);
}

/// <summary>Bildirimler ve görev değişiklikleri.</summary>
[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.User(user.UserId()));
        if (user.HasPolicy(Policies.ViewTracking))
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Watchers(user.TenantId()));
        await base.OnConnectedAsync();
    }
}

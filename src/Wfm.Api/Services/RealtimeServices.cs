using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Hubs;
using Wfm.Application.Contracts;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

public class UserDirectory(WfmDbContext db)
{
    public async Task<Dictionary<Guid, string>> NamesAsync(Guid tenantId) =>
        await db.Users.Where(u => u.TenantId == tenantId).ToDictionaryAsync(u => u.Id, u => u.FullName);
}

/// <summary>Konum ping'lerini kaydeder ve izleyicilere canlı yayınlar.</summary>
public class TrackingService(WfmDbContext db, IHubContext<TrackingHub> hub)
{
    private static readonly WorkTaskStatus[] ActiveStatuses = [WorkTaskStatus.EnRoute, WorkTaskStatus.OnSite, WorkTaskStatus.Accepted];

    public async Task RecordAsync(Guid tenantId, Guid userId, IReadOnlyList<LocationPingDto> pings)
    {
        if (pings.Count == 0) return;

        var valid = pings.Where(p => p.Latitude is >= -90 and <= 90 && p.Longitude is >= -180 and <= 180).ToList();
        db.LocationPings.AddRange(valid.Select(p => new LocationPing
        {
            TenantId = tenantId, UserId = userId,
            Latitude = p.Latitude, Longitude = p.Longitude, Accuracy = p.Accuracy, Speed = p.Speed,
            Heading = p.Heading, BatteryLevel = p.BatteryLevel,
            RecordedAt = p.RecordedAt == default ? DateTime.UtcNow : p.RecordedAt.ToUniversalTime()
        }));
        await db.SaveChangesAsync();

        var latest = valid.MaxBy(p => p.RecordedAt);
        if (latest is null) return;
        var dto = await BuildWorkerLocationAsync(tenantId, userId, latest);
        if (dto is not null)
            await hub.Clients.Group(HubGroups.Watchers(tenantId)).SendAsync(HubMethods.LocationUpdated, dto);
    }

    private async Task<WorkerLocationDto?> BuildWorkerLocationAsync(Guid tenantId, Guid userId, LocationPingDto p)
    {
        var user = await db.Users.Where(u => u.Id == userId).Select(u => new { u.FullName }).FirstOrDefaultAsync();
        if (user is null) return null;
        var onShift = await db.Shifts.IgnoreQueryFilters().AnyAsync(s => s.UserId == userId && s.EndedAt == null);
        var task = await db.Tasks.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.AssigneeId == userId && ActiveStatuses.Contains(t.Status))
            .OrderByDescending(t => t.Status).Select(t => new { t.Id, t.Title }).FirstOrDefaultAsync();
        return new WorkerLocationDto(userId, user.FullName, p.Latitude, p.Longitude, p.RecordedAt, p.BatteryLevel, p.Speed,
            onShift, task?.Id, task?.Title);
    }
}

/// <summary>Bildirim kaydı + SignalR ile kullanıcıya iletim; görev değişikliklerini izleyicilere yayar.</summary>
public class NotificationService(WfmDbContext db, IHubContext<NotificationHub> hub)
{
    public async Task NotifyAsync(Guid tenantId, Guid userId, string title, string body, Guid? taskId)
    {
        var n = new Notification { TenantId = tenantId, UserId = userId, Title = title, Body = body, WorkTaskId = taskId };
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        await hub.Clients.Group(HubGroups.User(userId)).SendAsync(HubMethods.NotificationReceived, n.ToDto());
    }

    public async Task TaskChangedAsync(Guid tenantId, WorkTaskDto task, params Guid?[] extraUsers)
    {
        await hub.Clients.Group(HubGroups.Watchers(tenantId)).SendAsync(HubMethods.TaskChanged, task);
        var users = extraUsers.Append(task.AssigneeId).OfType<Guid>().Distinct().Select(HubGroups.User).ToList();
        if (users.Count > 0)
            await hub.Clients.Groups(users).SendAsync(HubMethods.TaskChanged, task);
    }
}

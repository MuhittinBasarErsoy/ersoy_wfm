using Wfm.Application.Contracts;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Services;

public static class Mapping
{
    public static UserDto ToDto(AppUser u, IEnumerable<string> roles, string tenantName) =>
        new(u.Id, u.Email!, u.FullName, u.PhoneNumber, u.TenantId, tenantName, roles.ToList(), u.IsActive, u.TeamId);

    public static TaskTypeDto ToDto(this TaskType t) =>
        new(t.Id, t.Name, t.Description, t.Icon, t.Color, t.IsActive, t.Fields.OrderBy(f => f.Order).ToList(), t.Completion);

    public static TeamDto ToDto(this Team t) => new(t.Id, t.Name, t.Description);

    public static NotificationDto ToDto(this Notification n) => new(n.Id, n.Title, n.Body, n.WorkTaskId, n.CreatedAt, n.ReadAt, n.Kind);

    public static TenantDto ToDto(this Tenant t) =>
        new(t.Id, t.Name, t.Slug, t.IsActive, t.DefaultLatitude, t.DefaultLongitude, t.BrandColor, t.LogoUrl);

    public static TaskCommentDto ToDto(this TaskComment c, IReadOnlyDictionary<Guid, string> userNames) =>
        new(c.Id, c.WorkTaskId, c.UserId, userNames.TryGetValue(c.UserId, out var n) ? n : null, c.Body, c.CreatedAt);

    public static WorkTaskDto ToDto(this WorkTask t, IReadOnlyDictionary<Guid, string> userNames) => new()
    {
        Id = t.Id,
        TaskTypeId = t.TaskTypeId,
        TaskTypeName = t.TaskType?.Name ?? "",
        TaskTypeColor = t.TaskType?.Color ?? "#1976d2",
        TaskTypeIcon = t.TaskType?.Icon ?? "assignment",
        Title = t.Title,
        Description = t.Description,
        Priority = t.Priority,
        Status = t.Status,
        AssigneeId = t.AssigneeId,
        AssigneeName = t.AssigneeId is { } a && userNames.TryGetValue(a, out var n) ? n : null,
        TeamId = t.TeamId,
        Address = t.Address,
        Latitude = t.Latitude,
        Longitude = t.Longitude,
        CustomerName = t.CustomerName,
        CustomerPhone = t.CustomerPhone,
        ScheduledStart = t.ScheduledStart,
        ScheduledEnd = t.ScheduledEnd,
        StartedAt = t.StartedAt,
        CompletedAt = t.CompletedAt,
        CompletionNote = t.CompletionNote,
        CustomFieldValues = t.CustomFieldValues,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        RowVersion = Convert.ToBase64String(t.RowVersion),
        TrackingToken = t.TrackingToken
    };

    public static WorkTaskDetailDto ToDetailDto(this WorkTask t, IReadOnlyDictionary<Guid, string> userNames, FileUrlSigner signer)
    {
        var dto = t.ToDto(userNames);
        return new WorkTaskDetailDto
        {
            Id = dto.Id, TaskTypeId = dto.TaskTypeId, TaskTypeName = dto.TaskTypeName, TaskTypeColor = dto.TaskTypeColor,
            TaskTypeIcon = dto.TaskTypeIcon, Title = dto.Title, Description = dto.Description, Priority = dto.Priority,
            Status = dto.Status, AssigneeId = dto.AssigneeId, AssigneeName = dto.AssigneeName, TeamId = dto.TeamId,
            Address = dto.Address, Latitude = dto.Latitude, Longitude = dto.Longitude, CustomerName = dto.CustomerName,
            CustomerPhone = dto.CustomerPhone, ScheduledStart = dto.ScheduledStart, ScheduledEnd = dto.ScheduledEnd,
            StartedAt = dto.StartedAt, CompletedAt = dto.CompletedAt, CompletionNote = dto.CompletionNote,
            CustomFieldValues = dto.CustomFieldValues, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt,
            RowVersion = dto.RowVersion, TrackingToken = dto.TrackingToken,
            TaskType = t.TaskType?.ToDto(),
            Events = t.Events.OrderBy(e => e.CreatedAt).Select(e => new TaskEventDto(e.Id, e.UserId,
                userNames.TryGetValue(e.UserId, out var un) ? un : null, e.FromStatus, e.ToStatus, e.Note,
                e.Latitude, e.Longitude, e.CreatedAt)).ToList(),
            Attachments = t.Attachments.OrderBy(a => a.CapturedAt).Select(a => new AttachmentDto(a.Id, a.Kind, a.FileName,
                a.ContentType, signer.CreateUrl(a.Id), a.Latitude, a.Longitude, a.CapturedAt, a.UploadedById)).ToList()
        };
    }
}

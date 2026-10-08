using Wfm.Application.Contracts;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Services;

public static class Mapping
{
    public static UserDto ToDto(AppUser u, IEnumerable<string> roles, string tenantName) =>
        new(u.Id, u.Email!, u.FullName, u.PhoneNumber, u.TenantId, tenantName, roles.ToList(), u.IsActive, u.TeamId);

    public static TaskTypeDto ToDto(this TaskType t) =>
        new(t.Id, t.Name, t.Description, t.Icon, t.Color, t.IsActive, t.Fields.OrderBy(f => f.Order).ToList(), t.Completion,
            t.Stages.ToList(), t.RequiresVisit);

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
        Stage = t.Stage,
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
        TrackingToken = t.TrackingToken,
        JobId = t.JobId,
        StepOrder = t.StepOrder,
        PlannedAssigneeId = t.PlannedAssigneeId,
        PlannedAssigneeName = t.PlannedAssigneeId is { } p && userNames.TryGetValue(p, out var pn) ? pn : null
    };

    public static WorkTaskDetailDto ToDetailDto(this WorkTask t, IReadOnlyDictionary<Guid, string> userNames, FileUrlSigner signer)
    {
        var dto = t.ToDto(userNames);
        return new WorkTaskDetailDto
        {
            Id = dto.Id, TaskTypeId = dto.TaskTypeId, TaskTypeName = dto.TaskTypeName, TaskTypeColor = dto.TaskTypeColor,
            TaskTypeIcon = dto.TaskTypeIcon, Title = dto.Title, Description = dto.Description, Priority = dto.Priority,
            Status = dto.Status, Stage = dto.Stage, AssigneeId = dto.AssigneeId, AssigneeName = dto.AssigneeName, TeamId = dto.TeamId,
            Address = dto.Address, Latitude = dto.Latitude, Longitude = dto.Longitude, CustomerName = dto.CustomerName,
            CustomerPhone = dto.CustomerPhone, ScheduledStart = dto.ScheduledStart, ScheduledEnd = dto.ScheduledEnd,
            StartedAt = dto.StartedAt, CompletedAt = dto.CompletedAt, CompletionNote = dto.CompletionNote,
            CustomFieldValues = dto.CustomFieldValues, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt,
            RowVersion = dto.RowVersion, TrackingToken = dto.TrackingToken,
            JobId = dto.JobId, StepOrder = dto.StepOrder, PlannedAssigneeId = dto.PlannedAssigneeId,
            PlannedAssigneeName = dto.PlannedAssigneeName,
            TaskType = t.TaskType?.ToDto(),
            Events = t.Events.OrderBy(e => e.CreatedAt).Select(e => new TaskEventDto(e.Id, e.UserId,
                userNames.TryGetValue(e.UserId, out var un) ? un : null, e.FromStatus, e.ToStatus, e.Note,
                e.Latitude, e.Longitude, e.CreatedAt, e.Stage)).ToList(),
            Attachments = t.Attachments.OrderBy(a => a.CapturedAt).Select(a => new AttachmentDto(a.Id, a.Kind, a.FileName,
                a.ContentType, signer.CreateUrl(a.Id), a.Latitude, a.Longitude, a.CapturedAt, a.UploadedById)).ToList()
        };
    }

    public static JobTemplateDto ToDto(this JobTemplate t) =>
        new(t.Id, t.Name, t.Description, t.Steps.OrderBy(s => s.Order).Select(s => new JobStepInput
        {
            Order = s.Order, TaskTypeId = s.TaskTypeId, Title = s.Title, Description = s.Description, AssigneeId = s.DefaultAssigneeId
        }).ToList());

    /// <summary>İş özeti; <paramref name="j"/>.Tasks yüklenmiş olmalı.</summary>
    public static JobDto ToDto(this Job j, IReadOnlyDictionary<Guid, string> userNames) => new()
    {
        Id = j.Id, Title = j.Title, Description = j.Description, Status = j.Status, CurrentOrder = j.CurrentOrder,
        OrderCount = j.OrderCount,
        StepCount = j.Tasks.Count(t => t.StepOrder != null),
        DoneSteps = j.Tasks.Count(t => t.StepOrder != null && t.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled),
        CustomerName = j.CustomerName, CustomerPhone = j.CustomerPhone, Address = j.Address,
        Latitude = j.Latitude, Longitude = j.Longitude, CreatedAt = j.CreatedAt, UpdatedAt = j.UpdatedAt, CompletedAt = j.CompletedAt,
        ActiveAssignees = j.Status is JobStatus.Completed or JobStatus.Cancelled ? [] :
            j.Tasks.Where(t => t.StepOrder == j.CurrentOrder && t.IsOpen && t.AssigneeId is { } a && userNames.ContainsKey(a))
                .Select(t => userNames[t.AssigneeId!.Value]).Distinct().ToList()
    };

    public static JobDetailDto ToDetailDto(this Job j, IReadOnlyDictionary<Guid, string> userNames)
    {
        var dto = j.ToDto(userNames);
        return new JobDetailDto
        {
            Id = dto.Id, Title = dto.Title, Description = dto.Description, Status = dto.Status, CurrentOrder = dto.CurrentOrder,
            OrderCount = dto.OrderCount, StepCount = dto.StepCount, DoneSteps = dto.DoneSteps, CustomerName = dto.CustomerName,
            CustomerPhone = dto.CustomerPhone, Address = dto.Address, Latitude = dto.Latitude, Longitude = dto.Longitude,
            CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt, CompletedAt = dto.CompletedAt, ActiveAssignees = dto.ActiveAssignees,
            Steps = j.Tasks.OrderBy(t => t.StepOrder ?? int.MaxValue).ThenBy(t => t.CreatedAt).Select(t => t.ToDto(userNames)).ToList()
        };
    }
}

using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Application.Contracts;

// ---------- Auth ----------
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserDto User);

// ---------- Tenants ----------
public record TenantDto(Guid Id, string Name, string Slug, bool IsActive, double DefaultLatitude, double DefaultLongitude);
public record CreateTenantRequest(string Name, string Slug, string AdminEmail, string AdminPassword, string AdminFullName);

// ---------- Users / Teams ----------
public record UserDto(Guid Id, string Email, string FullName, string? Phone, Guid TenantId, string TenantName,
    IReadOnlyList<string> Roles, bool IsActive, Guid? TeamId);

public record CreateUserRequest(string Email, string Password, string FullName, string? Phone, string Role, Guid? TeamId);
public record UpdateUserRequest(string FullName, string? Phone, string Role, bool IsActive, Guid? TeamId, string? NewPassword);

public record TeamDto(Guid Id, string Name, string? Description);
public record SaveTeamRequest(string Name, string? Description);

// ---------- Task types ----------
public record TaskTypeDto(Guid Id, string Name, string? Description, string Icon, string Color, bool IsActive,
    List<FieldDefinition> Fields, CompletionRequirements Completion);

public record SaveTaskTypeRequest(string Name, string? Description, string Icon, string Color, bool IsActive,
    List<FieldDefinition> Fields, CompletionRequirements Completion);

// ---------- Tasks ----------
public record WorkTaskDto
{
    public Guid Id { get; init; }
    public Guid TaskTypeId { get; init; }
    public string TaskTypeName { get; init; } = "";
    public string TaskTypeColor { get; init; } = "";
    public string TaskTypeIcon { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public TaskPriority Priority { get; init; }
    public WorkTaskStatus Status { get; init; }
    public Guid? AssigneeId { get; init; }
    public string? AssigneeName { get; init; }
    public Guid? TeamId { get; init; }
    public string Address { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public DateTime? ScheduledStart { get; init; }
    public DateTime? ScheduledEnd { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? CompletionNote { get; init; }
    public Dictionary<string, string?> CustomFieldValues { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string RowVersion { get; init; } = "";
}

public record WorkTaskDetailDto : WorkTaskDto
{
    public TaskTypeDto? TaskType { get; init; }
    public List<TaskEventDto> Events { get; init; } = [];
    public List<AttachmentDto> Attachments { get; init; } = [];
}

public record TaskEventDto(Guid Id, Guid UserId, string? UserName, WorkTaskStatus FromStatus, WorkTaskStatus ToStatus,
    string? Note, double? Latitude, double? Longitude, DateTime CreatedAt);

public record AttachmentDto(Guid Id, AttachmentKind Kind, string FileName, string ContentType, string Url,
    double? Latitude, double? Longitude, DateTime CapturedAt, Guid UploadedById);

public record SaveWorkTaskRequest
{
    public Guid TaskTypeId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public Guid? AssigneeId { get; set; }
    public Guid? TeamId { get; set; }
    public string Address { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public Dictionary<string, string?> CustomFieldValues { get; set; } = [];
    /// <summary>Güncellemede eşzamanlılık kontrolü için (base64).</summary>
    public string? RowVersion { get; set; }
}

public record AssignRequest(Guid? AssigneeId);

public record ChangeStatusRequest
{
    public WorkTaskStatus Status { get; set; }
    public string? Note { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Tamamlamada saha çalışanının doldurduğu alanlar.</summary>
    public Dictionary<string, string?>? CustomFieldValues { get; set; }
    /// <summary>Offline'da işlemin cihazda yapıldığı an.</summary>
    public DateTime? ClientTimestamp { get; set; }
}

public record TaskQuery
{
    public List<WorkTaskStatus>? Statuses { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? TaskTypeId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Search { get; set; }
    public bool OnlyOpen { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public record PagedResult<T>(List<T> Items, int Total);

// ---------- Tracking ----------
public record LocationPingDto(double Latitude, double Longitude, double? Accuracy, double? Speed, double? Heading,
    int? BatteryLevel, DateTime RecordedAt);

public record WorkerLocationDto(Guid UserId, string FullName, double Latitude, double Longitude, DateTime RecordedAt,
    int? BatteryLevel, double? Speed, bool OnShift, Guid? ActiveTaskId, string? ActiveTaskTitle);

public record ShiftDto(Guid Id, DateTime StartedAt, DateTime? EndedAt);

// ---------- Notifications ----------
public record NotificationDto(Guid Id, string Title, string Body, Guid? WorkTaskId, DateTime CreatedAt, DateTime? ReadAt);

// ---------- Sync ----------
public record SyncResponse(DateTime ServerTime, List<WorkTaskDetailDto> Tasks, List<TaskTypeDto> TaskTypes,
    List<NotificationDto> Notifications, ShiftDto? ActiveShift);

// ---------- Reports ----------
public record ReportSummaryDto(
    Dictionary<WorkTaskStatus, int> CountsByStatus,
    int CompletedToday,
    int CreatedToday,
    double? AvgCompletionMinutes,
    int ActiveWorkers,
    List<WorkerStatsDto> Workers,
    List<DailyCountDto> Last7Days);

public record WorkerStatsDto(Guid UserId, string FullName, int Completed, int Failed, int Open, double? AvgCompletionMinutes);
public record DailyCountDto(DateOnly Day, int Created, int Completed);

// ---------- Errors ----------
public record ApiError(string Message, Dictionary<string, string[]>? Errors = null);

// ---------- SignalR ----------
public static class HubPaths
{
    public const string Tracking = "/hubs/tracking";
    public const string Notifications = "/hubs/notifications";
}

public static class HubMethods
{
    // Sunucu → istemci
    public const string LocationUpdated = "LocationUpdated";
    public const string TaskChanged = "TaskChanged";
    public const string NotificationReceived = "NotificationReceived";
    public const string ShiftChanged = "ShiftChanged";
    // İstemci → sunucu
    public const string SendLocation = "SendLocation";
}

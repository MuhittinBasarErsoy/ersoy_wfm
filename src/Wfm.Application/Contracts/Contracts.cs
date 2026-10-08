using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Application.Contracts;

// ---------- Auth ----------
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserDto User);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record UpdateProfileRequest(string FullName, string? Phone);
public record ForgotPasswordRequest(string Email);

// ---------- Kayıtlı filtreler ----------
public record SavedFilterDto(Guid Id, string Name, string Query, bool IsShared, bool IsMine, string? OwnerName);
public record SaveFilterRequest(string Name, string Query, bool IsShared);

/// <summary>Giriş ekranında gösterilen şirket markası (oturum gerektirmez).</summary>
public record PublicTenantBrandDto(string Name, string Slug, string? BrandColor, string? LogoUrl);

// ---------- Tenants ----------
public record TenantDto(Guid Id, string Name, string Slug, bool IsActive, double DefaultLatitude, double DefaultLongitude,
    string? BrandColor = null, string? LogoUrl = null);
public record UpdateTenantSettingsRequest(string Name, double DefaultLatitude, double DefaultLongitude, string? BrandColor, string? LogoUrl);
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
    List<FieldDefinition> Fields, CompletionRequirements Completion, List<string>? Stages = null, bool RequiresVisit = true)
{
    public List<string> Stages { get; init; } = Stages ?? [];
}

public record SaveTaskTypeRequest(string Name, string? Description, string Icon, string Color, bool IsActive,
    List<FieldDefinition> Fields, CompletionRequirements Completion, List<string>? Stages = null, bool RequiresVisit = true);

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
    /// <summary>Görev tipinde tanımlı ara aşama (ör. "Okunda").</summary>
    public string? Stage { get; init; }
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
    public string? TrackingToken { get; init; }

    /// <summary>Planlanan bitiş zamanı geçtiği hâlde açık.</summary>
    public bool IsOverdue => Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Failed)
                             && ScheduledEnd is { } end && end < DateTime.UtcNow;

    /// <summary>Masa başı görevlerde konum seçilmemiş olabilir; haritada gösterilmez.</summary>
    public bool HasLocation => Latitude != 0 || Longitude != 0;
}

public record WorkTaskDetailDto : WorkTaskDto
{
    public TaskTypeDto? TaskType { get; init; }
    public List<TaskEventDto> Events { get; init; } = [];
    public List<AttachmentDto> Attachments { get; init; } = [];
}

public record TaskEventDto(Guid Id, Guid UserId, string? UserName, WorkTaskStatus FromStatus, WorkTaskStatus ToStatus,
    string? Note, double? Latitude, double? Longitude, DateTime CreatedAt, string? Stage = null);

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

/// <summary>Ara aşamayı değiştirir; Stage null/boş ise aşama temizlenir.</summary>
public record ChangeStageRequest(string? Stage, string? Note = null, DateTime? ClientTimestamp = null);

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
    public string? Stage { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Search { get; set; }
    public bool OnlyOpen { get; set; }
    /// <summary>Yalnızca planlanan bitişi geçmiş açık görevler.</summary>
    public bool Overdue { get; set; }
    /// <summary>Sıralama alanı: priority (varsayılan), scheduled, created, updated, title, status, assignee.</summary>
    public string? Sort { get; set; }
    public bool Desc { get; set; }
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
public record NotificationDto(Guid Id, string Title, string Body, Guid? WorkTaskId, DateTime CreatedAt, DateTime? ReadAt,
    string Kind = "general");

// ---------- Comments ----------
public record TaskCommentDto(Guid Id, Guid WorkTaskId, Guid UserId, string? UserName, string Body, DateTime CreatedAt);
public record AddCommentRequest(string Body);

// ---------- Public tracking (müşteri) ----------
public record TrackingLinkDto(string Token);
public record PublicTrackingEventDto(WorkTaskStatus Status, DateTime At);
public record PublicTrackingDto(
    string TenantName, string? BrandColor, string? LogoUrl,
    string Title, WorkTaskStatus Status, string Address, double Latitude, double Longitude,
    DateTime? ScheduledStart, DateTime? ScheduledEnd, DateTime? CompletedAt,
    string? WorkerFirstName, double? WorkerLatitude, double? WorkerLongitude, DateTime? WorkerSeenAt,
    List<PublicTrackingEventDto> Events);

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
    List<DailyCountDto> Last7Days,
    int OverdueCount = 0,
    int UnassignedCount = 0,
    List<DailyCountDto>? Daily = null,
    List<TypeStatsDto>? Types = null);

public record TypeStatsDto(Guid TaskTypeId, string Name, string Color, int Created, int Completed, int Failed, double? AvgCompletionMinutes);

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
    public const string CommentAdded = "CommentAdded";
    // İstemci → sunucu
    public const string SendLocation = "SendLocation";
}

using Wfm.Domain.Common;

namespace Wfm.Domain.Entities;

public class VehicleObserverAccess : TenantEntity
{
    public Guid UserId { get; set; }
    public bool IsEnabled { get; set; }
    public bool AllVehicles { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? WeeklyScheduleJson { get; set; }
    public int SessionDurationMinutes { get; set; } = 30;
}

public class VehicleObserverSession : TenantEntity
{
    public Guid UserId { get; set; }
    public string FullName { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
}

public class VehicleObserverVehicle : TenantEntity
{
    public Guid ObserverId { get; set; }
    public Guid VehicleUserId { get; set; }
}

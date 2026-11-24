using Wfm.Domain.Common;

namespace Wfm.Domain.Entities;

public class VehicleObserverAccess : TenantEntity
{
    public Guid UserId { get; set; }
    public bool IsEnabled { get; set; }
    public bool AllVehicles { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class VehicleObserverVehicle : TenantEntity
{
    public Guid ObserverId { get; set; }
    public Guid VehicleUserId { get; set; }
}

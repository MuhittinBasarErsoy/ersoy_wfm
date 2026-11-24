using Microsoft.AspNetCore.Identity;

namespace Wfm.Infrastructure.Identity;

public class AppUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Guid? TeamId { get; set; }
    public bool VehicleTrackingEnabled { get; set; }
    public string? VehiclePlate { get; set; }
    public string? VehicleLabel { get; set; }
    public DateTime? VehicleTrackingEnabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppRole : IdentityRole<Guid>
{
    public AppRole() { }
    public AppRole(string name) : base(name) { }
}

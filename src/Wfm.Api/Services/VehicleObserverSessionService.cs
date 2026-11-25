using Microsoft.EntityFrameworkCore;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

public class VehicleObserverSessionService(WfmDbContext db)
{
    public const string ClaimName = "wfm.vehicle-session";

    public async Task<VehicleObserverSession?> FindActiveAsync(Guid id, Guid tenantId, Guid userId)
    {
        var session = await db.VehicleObserverSessions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId && s.UserId == userId);
        if (session is null || session.EndedAt is not null) return null;
        if (session.ExpiresAt > DateTime.UtcNow) return session;
        session.EndedAt = session.ExpiresAt;
        session.EndReason = "Expired";
        await db.SaveChangesAsync();
        return null;
    }

    public async Task CloseAsync(Guid id, Guid userId, string reason)
    {
        var session = await db.VehicleObserverSessions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (session is null || session.EndedAt is not null) return;
        var expired = session.ExpiresAt <= DateTime.UtcNow;
        session.EndedAt = expired ? session.ExpiresAt : DateTime.UtcNow;
        session.EndReason = expired ? "Expired" : reason;
    }
}

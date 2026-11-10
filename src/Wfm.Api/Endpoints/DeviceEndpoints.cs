using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

/// <summary>Push bildirimi için telefon kaydı. Aynı telefona başka biri giriş yaparsa kayıt ona geçer.</summary>
public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/devices").WithTags("Devices").RequireAuthorization();

        g.MapPost("/", async (RegisterDeviceRequest req, HttpContext ctx, WfmDbContext db, ApnsClient apns) =>
        {
            var token = req.Token?.Trim().ToLowerInvariant() ?? "";
            if (req.Platform != DevicePlatforms.Ios) return Results.BadRequest(new ApiError("Desteklenmeyen platform."));
            if (token.Length is < 32 or > 200 || !token.All(Uri.IsHexDigit)) return Results.BadRequest(new ApiError("Geçersiz cihaz anahtarı."));

            // Anahtar telefona aittir; kiracı filtresini aşıp kim kayıtlıysa yeni kullanıcıya devredilir.
            var device = await db.DeviceRegistrations.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Token == token);
            if (device is null)
            {
                device = new DeviceRegistration { Token = token, Platform = req.Platform };
                db.DeviceRegistrations.Add(device);
            }
            device.TenantId = ctx.User.TenantId();
            device.UserId = ctx.User.UserId();
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new DeviceRegistrationDto(apns.IsEnabled));
        });

        // Çıkışta çağrılır: telefon artık bu kişinin bildirimlerini almaz.
        g.MapDelete("/{token}", async (string token, HttpContext ctx, WfmDbContext db) =>
        {
            var uid = ctx.User.UserId();
            var normalized = token.Trim().ToLowerInvariant();
            await db.DeviceRegistrations.Where(d => d.Token == normalized && d.UserId == uid).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }
}

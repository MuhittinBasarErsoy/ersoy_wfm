using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Common;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Endpoints;

/// <summary>Devriye rotaları (bekçi turları), üretilen turlar ve kontrol noktası basışları.</summary>
public static class PatrolEndpoints
{
    private const int MaxCheckpoints = 50;
    private const int MaxWindows = 12;

    public static void MapPatrolEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- Rotalar ----------
        var routes = app.MapGroup("/api/patrol-routes").WithTags("Patrol").RequireAuthorization(Policies.ViewTracking);

        routes.MapGet("/", async (HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var names = await dir.NamesAsync(ctx.User.TenantId());
            return (await db.PatrolRoutes.OrderBy(r => r.Name).ToListAsync()).Select(r => r.ToDto(names)).ToList();
        });

        routes.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
            await db.PatrolRoutes.FirstOrDefaultAsync(r => r.Id == id) is { } r
                ? Results.Ok(r.ToDto(await dir.NamesAsync(ctx.User.TenantId()))) : Results.NotFound());

        routes.MapPost("/", async (SavePatrolRouteRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var errors = await ValidateAsync(req, db, ctx.User);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
            var route = new PatrolRoute { TenantId = ctx.User.TenantId(), CreatedById = ctx.User.UserId() };
            Apply(route, req);
            db.PatrolRoutes.Add(route);
            await db.SaveChangesAsync();
            return Results.Ok(route.ToDto(await dir.NamesAsync(route.TenantId)));
        }).RequireAuthorization(Policies.ManageTasks);

        routes.MapPut("/{id:guid}", async (Guid id, SavePatrolRouteRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var route = await db.PatrolRoutes.FirstOrDefaultAsync(r => r.Id == id);
            if (route is null) return Results.NotFound();
            var errors = await ValidateAsync(req, db, ctx.User);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
            Apply(route, req);
            await RemoveUpcomingRoundsAsync(db, id); // zamanlayıcı yeni ayarlarla yeniden üretir
            await db.SaveChangesAsync();
            return Results.Ok(route.ToDto(await dir.NamesAsync(route.TenantId)));
        }).RequireAuthorization(Policies.ManageTasks);

        // Geçmiş turlar ve basışlar rapor için kalır; yalnızca henüz başlamamış turlar silinir.
        routes.MapDelete("/{id:guid}", async (Guid id, WfmDbContext db) =>
        {
            var route = await db.PatrolRoutes.FirstOrDefaultAsync(r => r.Id == id);
            if (route is null) return Results.NotFound();
            db.PatrolRoutes.Remove(route);
            await RemoveUpcomingRoundsAsync(db, id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageTasks);

        // ---------- Turlar ----------
        var rounds = app.MapGroup("/api/patrol/rounds").WithTags("Patrol").RequireAuthorization();

        rounds.MapGet("/", async ([AsParameters] PatrolRoundQueryParams q, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var query = Scope(db.PatrolRounds.Include(r => r.Visits), ctx.User);
            if (q.From is { } f)
            {
                var from = LocalDayStart(f);
                query = query.Where(r => r.WindowStart >= from);
            }
            if (q.To is { } t)
            {
                var to = LocalDayStart(t.AddDays(1));
                query = query.Where(r => r.WindowStart < to);
            }
            if (q.AssigneeId is { } a) query = query.Where(r => r.AssigneeId == a);
            if (q.RouteId is { } rid) query = query.Where(r => r.RouteId == rid);
            if (q.Status is { } s) query = query.Where(r => r.Status == s);
            var list = await query.OrderByDescending(r => r.WindowStart).Take(2000).AsSplitQuery().ToListAsync();
            var names = await dir.NamesAsync(ctx.User.TenantId());
            return list.Select(r => r.ToDto(names)).ToList();
        });

        rounds.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
            await Scope(db.PatrolRounds.Include(r => r.Visits), ctx.User).FirstOrDefaultAsync(r => r.Id == id) is { } r
                ? Results.Ok(r.ToDto(await dir.NamesAsync(ctx.User.TenantId()))) : Results.NotFound());

        rounds.MapPost("/{id:guid}/visits", async (Guid id, PatrolVisitRequest req, HttpContext ctx, WfmDbContext db, UserDirectory dir) =>
        {
            var uid = ctx.User.UserId();
            if (req.Latitude is < -90 or > 90 || req.Longitude is < -180 or > 180 || (req.Latitude == 0 && req.Longitude == 0))
                return Results.BadRequest(new ApiError("Konum alınamadı. Konum izninin açık olduğundan emin olun."));

            // Offline'da basış anı cihazdan gelir; gelecekteki zamanlara izin verilmez.
            var now = DateTime.UtcNow;
            var at = req.ClientTimestamp?.ToUniversalTime() is { } ts && ts < now ? ts : now;

            for (var attempt = 0; ; attempt++)
            {
                var round = await db.PatrolRounds.Include(r => r.Visits).FirstOrDefaultAsync(r => r.Id == id);
                if (round is null) return Results.NotFound();
                if (round.AssigneeId != uid) return Results.Json(new ApiError("Bu tur size atanmamış."), statusCode: 403);

                // Offline tekrar gönderimi: aynı basış zaten kaydedildiyse başarı dön (idempotent).
                if (req.ClientId is { } cid && round.Visits.Any(v => v.ClientId == cid))
                    return Results.Ok(round.ToDto(await dir.NamesAsync(round.TenantId)));

                db.PatrolVisits.Add(round.RecordVisit(req.CheckpointId, uid, req.Latitude, req.Longitude, req.Accuracy, at, req.ClientId));
                try
                {
                    await db.SaveChangesAsync();
                    return Results.Ok(round.ToDto(await dir.NamesAsync(round.TenantId)));
                }
                catch (DbUpdateConcurrencyException) when (attempt < 3)
                {
                    // Zamanlayıcı turu aynı anda güncelledi (hatırlatma/kapatma): güncel hâliyle tekrar dene.
                    db.ChangeTracker.Clear();
                }
            }
        }).RequireAuthorization(Policies.FieldWork);
    }

    /// <summary>Saha çalışanının açık ve yakın zamandaki turları (sync ve saha ekranı için).</summary>
    public static async Task<List<PatrolRoundDto>> MyRoundsAsync(WfmDbContext db, Guid userId, IReadOnlyDictionary<Guid, string> names)
    {
        var now = DateTime.UtcNow;
        var from = now.AddHours(-12);
        var to = now.AddHours(24);
        var list = await db.PatrolRounds.Include(r => r.Visits)
            .Where(r => r.AssigneeId == userId && r.WindowEnd >= from && r.WindowStart < to)
            .OrderBy(r => r.WindowStart).AsSplitQuery().ToListAsync();
        return list.Select(r => r.ToDto(names)).ToList();
    }

    private static IQueryable<PatrolRound> Scope(IQueryable<PatrolRound> q, ClaimsPrincipal user)
    {
        if (user.HasPolicy(Policies.ViewTracking)) return q;
        var uid = user.UserId();
        return q.Where(r => r.AssigneeId == uid);
    }

    private static DateTime LocalDayStart(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), PatrolSchedule.Zone);

    private static async Task RemoveUpcomingRoundsAsync(WfmDbContext db, Guid routeId)
    {
        var now = DateTime.UtcNow;
        var upcoming = await db.PatrolRounds.Where(r => r.RouteId == routeId && r.Status == PatrolRoundStatus.Scheduled &&
                                                        r.WindowStart > now && !r.Visits.Any()).ToListAsync();
        db.PatrolRounds.RemoveRange(upcoming);
    }

    private static async Task<List<string>> ValidateAsync(SavePatrolRouteRequest req, WfmDbContext db, ClaimsPrincipal user)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.Name)) errors.Add("Rota adı zorunlu.");
        if (req.AssigneeId == Guid.Empty || await TaskEndpoints.InvalidAssignee(db, user, req.AssigneeId))
            errors.Add("Geçerli bir saha çalışanı seçin.");
        if (req.Windows.Count == 0) errors.Add("En az bir zaman aralığı ekleyin.");
        if (req.Windows.Count > MaxWindows) errors.Add($"En fazla {MaxWindows} zaman aralığı eklenebilir.");
        if (req.Windows.Any(w => w.Start == w.End)) errors.Add("Zaman aralığının başlangıcı ve bitişi aynı olamaz.");
        if (req.Checkpoints.Count == 0) errors.Add("En az bir kontrol noktası ekleyin.");
        if (req.Checkpoints.Count > MaxCheckpoints) errors.Add($"En fazla {MaxCheckpoints} kontrol noktası eklenebilir.");
        if (req.Checkpoints.Any(c => string.IsNullOrWhiteSpace(c.Name))) errors.Add("Her kontrol noktasına bir ad verin.");
        if (req.Checkpoints.Any(c => c.Latitude is < -90 or > 90 || c.Longitude is < -180 or > 180 || (c.Latitude == 0 && c.Longitude == 0)))
            errors.Add("Her kontrol noktası için haritadan konum seçin.");
        if (req.Checkpoints.Any(c => c.RadiusMeters is < 10 or > 1000)) errors.Add("Yarıçap 10–1000 m arasında olmalı.");
        if (req.ReminderMinutesBeforeEnd is < 0 or > 240) errors.Add("Hatırlatma süresi 0–240 dakika arasında olmalı.");
        if (OverlapsItself(req.Windows)) errors.Add("Zaman aralıkları birbiriyle çakışıyor.");
        return errors;
    }

    /// <summary>Aynı gün içindeki pencereler çakışmamalı (gece yarısını aşanlar 24 saat ötelenerek karşılaştırılır).</summary>
    private static bool OverlapsItself(List<PatrolWindow> windows)
    {
        var spans = windows.Where(w => w.Start != w.End).Select(w =>
        {
            var s = w.Start.ToTimeSpan();
            var e = w.End.ToTimeSpan();
            return (s, e: e <= s ? e + TimeSpan.FromDays(1) : e);
        }).OrderBy(x => x.s).ToList();
        for (var i = 1; i < spans.Count; i++)
            if (spans[i].s < spans[i - 1].e) return true;
        // Gece yarısını aşan son pencere ertesi günün ilk penceresine taşmasın.
        return spans.Count > 1 && spans[^1].e - TimeSpan.FromDays(1) > spans[0].s;
    }

    private static void Apply(PatrolRoute r, SavePatrolRouteRequest req)
    {
        r.Name = req.Name.Trim();
        r.Description = req.Description;
        r.AssigneeId = req.AssigneeId;
        r.IsActive = req.IsActive;
        r.EnforceOrder = req.EnforceOrder;
        r.Days = req.Days.Distinct().Count() == 7 ? [] : req.Days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
        r.Windows = req.Windows.OrderBy(w => w.Start).Select(w => new PatrolWindow { Start = w.Start, End = w.End }).ToList();
        r.ReminderMinutesBeforeEnd = req.ReminderMinutesBeforeEnd;
        // Mevcut noktaların kimliği korunur ki geçmiş turlardaki basışlar eşleşmeye devam etsin.
        r.Checkpoints = req.Checkpoints.Select((c, i) => new PatrolCheckpoint
        {
            Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id,
            Order = i + 1,
            Name = c.Name.Trim(),
            Latitude = c.Latitude,
            Longitude = c.Longitude,
            RadiusMeters = c.RadiusMeters
        }).ToList();
    }

    private static PatrolRouteDto ToDto(this PatrolRoute r, IReadOnlyDictionary<Guid, string> names) =>
        new(r.Id, r.Name, r.Description, r.AssigneeId, names.GetValueOrDefault(r.AssigneeId), r.IsActive, r.EnforceOrder,
            r.Days, r.Windows, r.ReminderMinutesBeforeEnd, r.Checkpoints.OrderBy(c => c.Order).ToList());

    public static PatrolRoundDto ToDto(this PatrolRound r, IReadOnlyDictionary<Guid, string> names) => new()
    {
        Id = r.Id,
        RouteId = r.RouteId,
        RouteName = r.RouteName,
        AssigneeId = r.AssigneeId,
        AssigneeName = names.GetValueOrDefault(r.AssigneeId),
        WindowStart = r.WindowStart,
        WindowEnd = r.WindowEnd,
        EnforceOrder = r.EnforceOrder,
        Status = r.Status,
        CompletedAt = r.CompletedAt,
        Checkpoints = r.Checkpoints.OrderBy(c => c.Order).Select(c =>
        {
            var v = r.Visits.FirstOrDefault(x => x.CheckpointId == c.Id);
            return new PatrolRoundCheckpointDto(c.Id, c.Order, c.Name, c.Latitude, c.Longitude, c.RadiusMeters, v?.VisitedAt, v?.DistanceMeters);
        }).ToList()
    };
}

public record PatrolRoundQueryParams(DateOnly? From, DateOnly? To, Guid? AssigneeId, Guid? RouteId, PatrolRoundStatus? Status);

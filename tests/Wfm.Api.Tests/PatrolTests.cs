using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;

namespace Wfm.Api.Tests;

/// <summary>Devriye (bekçi turu): pencere içinde kontrol noktalarına uğrayıp tuşa basma.</summary>
public class PatrolTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Admin = "yonetici@tamir.local";
    private const string Dispatcher = "dispecer@tamir.local";
    private const string Guard = "teknisyen1@tamir.local"; // Burak
    private const string Other = "teknisyen2@tamir.local"; // Selin

    private static readonly (string Name, double Lat, double Lng)[] Points =
        [("Kapı", 41.0400, 28.9800), ("Depo", 41.0410, 28.9810), ("Otopark", 41.0420, 28.9820)];

    [Fact]
    public async Task Guard_visits_checkpoints_in_order_and_round_completes()
    {
        var (dispatcher, guard, guardId) = await Setup();
        var route = await dispatcher.CreatePatrolRouteAsync(NewRoute("Sıralı tur", guardId, enforceOrder: true));
        var round = await StartRound(guard, route.Id);

        Assert.Equal(PatrolRoundStatus.InProgress, round.Status);
        Assert.True(round.IsActiveNow);
        Assert.Contains(await guard.GetNotificationsAsync(), n => n.Kind == NotificationKinds.PatrolStarting && n.Body.StartsWith("Sıralı tur"));

        var (first, second, third) = (round.Checkpoints[0], round.Checkpoints[1], round.Checkpoints[2]);

        // Uzaktan basılamaz.
        var far = await Assert.ThrowsAsync<ApiException>(() => guard.RecordPatrolVisitAsync(round.Id, At(first, 0.002)));
        Assert.Equal(HttpStatusCode.BadRequest, far.Status);
        Assert.Contains("uzaktasınız", far.Message);

        // Sıra zorunlu: ikinci noktaya önce basılamaz.
        var order = await Assert.ThrowsAsync<ApiException>(() => guard.RecordPatrolVisitAsync(round.Id, At(second)));
        Assert.Contains("Kapı", order.Message);

        var visit = At(first) with { ClientId = Guid.NewGuid() };
        await guard.RecordPatrolVisitAsync(round.Id, visit);
        var replay = await guard.RecordPatrolVisitAsync(round.Id, visit); // offline tekrar gönderim
        Assert.Equal(1, replay.VisitedCount);

        var dup = await Assert.ThrowsAsync<ApiException>(() => guard.RecordPatrolVisitAsync(round.Id, At(first)));
        Assert.Contains("zaten", dup.Message);

        await guard.RecordPatrolVisitAsync(round.Id, At(second));
        var done = await guard.RecordPatrolVisitAsync(round.Id, At(third));
        Assert.Equal(PatrolRoundStatus.Completed, done.Status);
        Assert.Equal(3, done.VisitedCount);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task Unfinished_round_is_closed_and_managers_are_notified()
    {
        var (dispatcher, guard, guardId) = await Setup();
        var route = await dispatcher.CreatePatrolRouteAsync(NewRoute("Serbest tur", guardId, enforceOrder: false));
        var round = await StartRound(guard, route.Id);

        // Serbest sırada son noktadan başlanabilir.
        await guard.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[2]));

        // Bitişe 15 dk kala hatırlatma, bitince kapanış.
        await RunScheduler(round.WindowEnd.AddMinutes(-10));
        Assert.Contains(await guard.GetNotificationsAsync(), n => n.Kind == NotificationKinds.PatrolEndingSoon && n.Body.Contains("2 nokta eksik"));

        await RunScheduler(round.WindowEnd.AddMinutes(1));
        var closed = await dispatcher.GetPatrolRoundAsync(round.Id);
        Assert.Equal(PatrolRoundStatus.Incomplete, closed.Status);
        Assert.Contains(await dispatcher.GetNotificationsAsync(), n => n.Kind == NotificationKinds.PatrolMissed && n.Body.Contains("1/3"));

        var report = await dispatcher.GetPatrolRoundsAsync(new PatrolRoundQuery(RouteId: route.Id, Status: PatrolRoundStatus.Incomplete));
        Assert.Contains(report, r => r.Id == round.Id);
    }

    [Fact]
    public async Task Rounds_are_private_and_route_is_validated()
    {
        var (dispatcher, guard, guardId) = await Setup();
        var route = await dispatcher.CreatePatrolRouteAsync(NewRoute("Gizli tur", guardId, enforceOrder: false));
        var round = await StartRound(guard, route.Id);

        var other = await api.LoginAs(Other);
        Assert.DoesNotContain((await other.SyncAsync()).PatrolRounds, r => r.Id == round.Id);
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => other.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[0])));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        await Assert.ThrowsAsync<ApiException>(() => guard.CreatePatrolRouteAsync(NewRoute("X", guardId, false)));

        var overlapping = NewRoute("Çakışan", guardId, false);
        overlapping.Windows = [new() { Start = new(9, 0), End = new(11, 0) }, new() { Start = new(10, 0), End = new(12, 0) }];
        var bad = await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreatePatrolRouteAsync(overlapping));
        Assert.Contains("çakışıyor", bad.Message);

        var empty = NewRoute("Boş", guardId, false);
        empty.Checkpoints = [];
        await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreatePatrolRouteAsync(empty));

        Assert.Contains(await dispatcher.GetPatrolRoutesAsync(), r => r.Name == "Depo ve otopark turu"); // demo verisi
    }

    [Fact]
    public async Task Any_team_member_can_work_a_team_round()
    {
        var (dispatcher, _, _) = await Setup();
        var admin = await api.LoginAs(Admin);
        var team = await admin.CreateTeamAsync(new SaveTeamRequest("Gece vardiyası", null));
        var (first, firstId) = await NewWorker(admin, "bekci-a@tamir.local", "Bekçi A", team.Id);
        var (second, _) = await NewWorker(admin, "bekci-b@tamir.local", "Bekçi B", team.Id);

        var request = NewRoute("Ekip turu", firstId, enforceOrder: false);
        request.AssigneeId = null;
        request.TeamId = team.Id;
        var route = await dispatcher.CreatePatrolRouteAsync(request);
        Assert.Null(route.AssigneeId);
        Assert.Equal("Gece vardiyası", route.TeamName);

        // Tur iki üyede de görünür, başlangıç bildirimi ikisine de gider.
        var round = await StartRound(first, route.Id);
        Assert.True(round.IsTeamRound);
        Assert.Equal("Gece vardiyası", round.OwnerName);
        Assert.Contains((await second.SyncAsync()).PatrolRounds, r => r.Id == round.Id);
        foreach (var member in new[] { first, second })
            Assert.Contains(await member.GetNotificationsAsync(), n => n.Kind == NotificationKinds.PatrolStarting && n.Body.StartsWith("Ekip turu"));

        // Ekip dışındakiler (ekipsiz çalışan dahil) görmez ve basamaz.
        var outsider = await api.LoginAs(Other);
        Assert.DoesNotContain((await outsider.SyncAsync()).PatrolRounds, r => r.Id == round.Id);
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => outsider.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[0])));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);

        // Üyeler farklı noktaları işaretleyebilir; kimin bastığı kayda geçer.
        await first.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[0]));
        var mid = await second.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[1]));
        Assert.Equal("Bekçi A", mid.Checkpoints[0].VisitedByName);
        Assert.Equal("Bekçi B", mid.Checkpoints[1].VisitedByName);

        // Aynı noktaya ikinci üye basarsa hata almaz, ilk basış geçerli kalır.
        var again = await second.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[0]));
        Assert.Equal("Bekçi A", again.Checkpoints[0].VisitedByName);
        Assert.Equal(2, again.VisitedCount);

        var done = await first.RecordPatrolVisitAsync(round.Id, At(round.Checkpoints[2]));
        Assert.Equal(PatrolRoundStatus.Completed, done.Status);

        // Rotası olan ekip silinemez.
        await Assert.ThrowsAsync<ApiException>(() => admin.DeleteTeamAsync(team.Id));
    }

    [Fact]
    public async Task Team_route_is_validated()
    {
        var (dispatcher, _, guardId) = await Setup();
        var admin = await api.LoginAs(Admin);
        var empty = await admin.CreateTeamAsync(new SaveTeamRequest("Boş ekip", null));

        var noWorkers = NewRoute("Üyesiz", guardId, false);
        noWorkers.AssigneeId = null;
        noWorkers.TeamId = empty.Id;
        var bad = await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreatePatrolRouteAsync(noWorkers));
        Assert.Contains("aktif saha çalışanı yok", bad.Message);

        var both = NewRoute("İkisi birden", guardId, false);
        both.TeamId = empty.Id;
        await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreatePatrolRouteAsync(both));

        var neither = NewRoute("Hiçbiri", guardId, false);
        neither.AssigneeId = null;
        await Assert.ThrowsAsync<ApiException>(() => dispatcher.CreatePatrolRouteAsync(neither));
    }

    /// <summary>Telefonlardaki eski sürümler AssigneeId'yi dolu Guid bekler; alan yoksa varsayılana düşer, null ise sync bozulur.</summary>
    [Fact]
    public void Team_round_omits_assignee_for_old_mobile_clients()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PatrolRoundDto { TeamId = Guid.NewGuid() }, WfmJson.Options);
        Assert.DoesNotContain("\"assigneeId\"", json);
    }

    [Fact]
    public void Windows_crossing_midnight_end_next_day()
    {
        var route = new PatrolRoute
        {
            Days = [DayOfWeek.Friday],
            Windows = [new() { Start = new(9, 0), End = new(11, 0) }, new() { Start = new(23, 0), End = new(1, 0) }]
        };
        var friday = new DateOnly(2026, 10, 23);
        var windows = PatrolSchedule.WindowsOn(route, friday).ToList();
        Assert.Equal(2, windows.Count);
        Assert.Equal(TimeSpan.FromHours(2), windows[1].End - windows[1].Start);
        Assert.Equal(new DateTime(2026, 10, 23, 6, 0, 0), windows[0].Start); // İstanbul UTC+3
        Assert.Empty(PatrolSchedule.WindowsOn(route, friday.AddDays(1)));
    }

    // ---------- Yardımcılar ----------
    private async Task<(WfmApiClient dispatcher, WfmApiClient guard, Guid guardId)> Setup()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var guard = await api.LoginAs(Guard);
        return (dispatcher, guard, (await guard.MeAsync()).Id);
    }

    private async Task<(WfmApiClient client, Guid id)> NewWorker(WfmApiClient admin, string email, string name, Guid teamId)
    {
        var user = await admin.CreateUserAsync(new CreateUserRequest(email, "Demo123!", name, null, Domain.Roles.FieldWorker, teamId));
        return (await api.LoginAs(email), user.Id);
    }

    /// <summary>Şu anı içine alan tek pencereli rota (yerel saatle 10 dk önce başlar, 50 dk sonra biter).</summary>
    private static SavePatrolRouteRequest NewRoute(string name, Guid assignee, bool enforceOrder)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PatrolSchedule.Zone);
        return new SavePatrolRouteRequest
        {
            Name = name, AssigneeId = assignee, EnforceOrder = enforceOrder,
            Windows = [new() { Start = TimeOnly.FromDateTime(local.AddMinutes(-10)), End = TimeOnly.FromDateTime(local.AddMinutes(50)) }],
            Checkpoints = Points.Select(p => new PatrolCheckpoint { Name = p.Name, Latitude = p.Lat, Longitude = p.Lng, RadiusMeters = 50 }).ToList()
        };
    }

    private async Task<PatrolRoundDto> StartRound(WfmApiClient guard, Guid routeId)
    {
        await RunScheduler(DateTime.UtcNow);
        return Assert.Single((await guard.SyncAsync()).PatrolRounds, r => r.RouteId == routeId && r.IsActiveNow);
    }

    private async Task RunScheduler(DateTime now)
    {
        using var scope = api.Services.CreateScope();
        await PatrolSchedulerService.RunOnceAsync(scope.ServiceProvider, now);
    }

    private static PatrolVisitRequest At(PatrolRoundCheckpointDto cp, double offset = 0) =>
        new(cp.Id, cp.Latitude + offset, cp.Longitude, 5);
}

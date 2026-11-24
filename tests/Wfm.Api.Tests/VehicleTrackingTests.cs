using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wfm.Api.Endpoints;
using Wfm.Api.Hubs;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Tests;

public class VehicleTrackingTests
{
    [Theory]
    [InlineData("GET", "/api/tasks")]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/auth/tenant")]
    [InlineData("PUT", "/api/auth/me")]
    [InlineData("GET", "/hubs/tracking/negotiate")]
    [InlineData("GET", "/api/vehicles-other")]
    [InlineData("POST", "/api/vehicles")]
    public async Task Observer_cannot_access_non_vehicle_endpoints(string method, string path)
    {
        await using var host = await Host.StartAsync();
        var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Observer_has_no_vehicle_access_by_default()
    {
        await using var host = await Host.StartAsync(grant: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/vehicles")).StatusCode);
    }

    [Fact]
    public async Task Selected_vehicle_scope_is_enforced_for_live_data_and_history()
    {
        await using var host = await Host.StartAsync();
        var snapshot = await host.Client.GetFromJsonAsync<VehicleSnapshotDto>("/api/vehicles");
        var visible = Assert.Single(snapshot!.Vehicles);
        Assert.Equal(Host.Vehicle, visible.UserId);
        Assert.Equal(36, visible.SpeedKmh);
        Assert.Equal("no-store", (await host.Client.GetAsync("/api/vehicles")).Headers.CacheControl!.ToString());
        var denied = await host.Client.GetAsync(HistoryUrl(Host.Unassigned));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(HistoryUrl(Host.OtherTenantVehicle))).StatusCode);
        var history = await host.Client.GetFromJsonAsync<VehicleHistoryDto>(HistoryUrl(Host.Vehicle));
        Assert.Single(history!.Points); // ordinary personnel positions and old plate positions stay private
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disabled_or_expired_access_is_revoked_on_the_next_request(bool expired)
    {
        await using var host = await Host.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/vehicles")).StatusCode);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
            var grant = await db.VehicleObserverAccesses.IgnoreQueryFilters().SingleAsync();
            if (expired) grant.ExpiresAt = DateTime.UtcNow.AddSeconds(-1); else grant.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/vehicles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync(HistoryUrl(Host.Vehicle))).StatusCode);
    }

    [Fact]
    public async Task All_vehicles_still_excludes_other_tenants_and_disabled_profiles()
    {
        await using var host = await Host.StartAsync(all: true);
        var result = await host.Client.GetFromJsonAsync<VehicleSnapshotDto>("/api/vehicles");
        Assert.Equal(2, result!.Vehicles.Count);
        Assert.DoesNotContain(result.Vehicles, v => v.UserId == Host.OtherTenantVehicle || v.UserId == Host.Disabled);
    }

    [Fact]
    public async Task Observer_cannot_edit_access_and_admin_cannot_grant_foreign_vehicles()
    {
        await using var host = await Host.StartAsync();
        var url = $"/api/vehicle-access/{Host.Observer}";
        var body = new SaveVehicleAccessRequest(true, false, null, [Host.OtherTenantVehicle]);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PutAsJsonAsync(url, body)).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Test-Role", Roles.TenantAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync(url, body with { VehicleUserIds = [Host.Unassigned] })).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("Test-Role");
        var result = await host.Client.GetFromJsonAsync<VehicleSnapshotDto>("/api/vehicles");
        Assert.Equal(Host.Unassigned, Assert.Single(result!.Vehicles).UserId);
    }

    [Fact]
    public async Task Recording_marks_only_enabled_on_shift_points_as_vehicle_data()
    {
        await using var host = await Host.StartAsync();
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == Host.Vehicle);
        user.VehicleTrackingEnabledAt = DateTime.UtcNow.AddMinutes(-30);
        db.Shifts.Add(new WorkShift { TenantId = Host.Tenant, UserId = Host.Vehicle, StartedAt = DateTime.UtcNow.AddMinutes(-15) });
        await db.SaveChangesAsync();
        var service = new TrackingService(db, scope.ServiceProvider.GetRequiredService<IHubContext<TrackingHub>>());
        var now = DateTime.UtcNow;
        await service.RecordAsync(Host.Tenant, Host.Vehicle, [new(40, 29, 5, 10, null, 75, now), new(40, 29, 5, 10, null, 75, now.AddHours(-1))]);
        var saved = await db.LocationPings.IgnoreQueryFilters().Where(p => p.UserId == Host.Vehicle && p.ReceivedAt >= now).ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.True(saved.Single(p => p.RecordedAt == now).IsVehicleLocation);
        Assert.False(saved.Single(p => p.RecordedAt < now).IsVehicleLocation);
    }

    [Fact]
    public void Route_breaks_on_gaps_bad_accuracy_and_impossible_jumps()
    {
        var t = DateTime.UtcNow;
        var data = VehicleTrackingRules.History([
            new(40, 29, t, 0, 5), new(40, 29, t.AddMinutes(3), 0, 5), new(40, 29, t.AddMinutes(6), 0, 5),
            new(41, 30, t.AddMinutes(7), 0, 5), new(41, 30, t.AddHours(1), 0, 5),
            new(41.01, 30, t.AddHours(1).AddMinutes(1), 30, 500), new(41.02, 30, t.AddHours(1).AddMinutes(2), 30, 5)
        ], false);
        Assert.Equal(0, data.ApproximateDistanceKm);
        Assert.Equal(0, data.MovingMinutes);
        Assert.Single(data.Stops);
        Assert.Equal(6, data.Points.Count);
        Assert.NotEqual(data.Points[2].Segment, data.Points[3].Segment);
        Assert.NotEqual(data.Points[3].Segment, data.Points[4].Segment);
    }

    [Fact]
    public void Live_vehicle_query_translates_to_sql_server_without_executing_it()
    {
        using var db = new WfmDbContext(new DbContextOptionsBuilder<WfmDbContext>()
            .UseSqlServer("Server=unused;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=True").Options, new NoTenant());
        var query = new VehicleAccessService(db).LatestLocations(db.Users.Where(u => u.TenantId == Host.Tenant && u.VehicleTrackingEnabled),
            Host.Tenant, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
        var sql = query.ToQueryString();
        Assert.Contains("ROW_NUMBER", sql);
        Assert.Contains("IsVehicleLocation", sql);
        Assert.Contains("VehicleTrackingEnabledAt", sql);
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => Host.Tenant;
        public Guid? UserId => Host.Observer;
        public bool IsSuperAdmin => false;
    }

    [Fact]
    public void Vehicle_role_has_no_personnel_or_task_permissions()
    {
        Assert.Contains(Roles.VehicleObserver, Policies.Map[Policies.ViewVehicles]);
        Assert.All(Policies.Map.Where(p => p.Key != Policies.ViewVehicles), p => Assert.DoesNotContain(Roles.VehicleObserver, p.Value));
        Assert.NotNull(VehicleTrackingRules.ValidateProfile(Roles.Viewer, true, "16 ABC 16", null));
        Assert.NotNull(VehicleTrackingRules.ValidateProfile(Roles.FieldWorker, true, "", null));
        Assert.Equal(36, VehicleTrackingRules.SpeedKmh(10));
        Assert.Null(VehicleTrackingRules.SpeedKmh(-1));
    }

    private static string HistoryUrl(Guid id) => $"/api/vehicles/{id}/history?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";

    // Isolated real HTTP endpoints; no live credentials, GPS data, SQL Server, or production services.
    private sealed class Host : IAsyncDisposable
    {
        public static readonly Guid Tenant = Guid.NewGuid(), OtherTenant = Guid.NewGuid(), Observer = Guid.NewGuid(),
            Vehicle = Guid.NewGuid(), Unassigned = Guid.NewGuid(), OtherTenantVehicle = Guid.NewGuid(), Disabled = Guid.NewGuid();
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public static async Task<Host> StartAsync(bool grant = true, bool all = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
            // Each context must share the host's database, not create its own per scope.
            var database = Guid.NewGuid().ToString();
            builder.Services.AddDbContext<WfmDbContext>(o => o.UseInMemoryDatabase(database));
            builder.Services.AddScoped<VehicleAccessService>();
            builder.Services.AddSignalR();
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
            builder.Services.AddAuthorization(o => { foreach (var (policy, roles) in Policies.Map) o.AddPolicy(policy, p => p.RequireRole(roles)); });
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.Use(VehicleObserverBoundary.InvokeAsync);
            app.MapVehicleEndpoints();
            app.MapGet("/api/tasks", () => Results.Ok("private")).RequireAuthorization();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
                db.Tenants.Add(new Tenant { Id = Tenant, Name = "Test", Slug = "test", LocationRetentionDays = 30 });
                db.Roles.Add(new AppRole(Roles.VehicleObserver) { Id = Observer });
                db.Users.Add(new AppUser { Id = Observer, TenantId = Tenant, FullName = "Denetçi" });
                db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = Observer, RoleId = Observer });
                foreach (var (id, tid, enabled, plate) in new[] {
                    (Vehicle, Tenant, true, "16 TEST 1"), (Unassigned, Tenant, true, "16 TEST 2"),
                    (OtherTenantVehicle, OtherTenant, true, "34 PRIVATE"), (Disabled, Tenant, false, "16 OFF") })
                {
                    db.Users.Add(new AppUser { Id = id, TenantId = tid, FullName = "Sürücü", VehicleTrackingEnabled = enabled,
                        VehiclePlate = plate, VehicleTrackingEnabledAt = DateTime.UtcNow.AddHours(-4) });
                    db.LocationPings.Add(new LocationPing { TenantId = tid, UserId = id, Latitude = 40, Longitude = 29,
                        Speed = 10, Accuracy = 5, RecordedAt = DateTime.UtcNow.AddMinutes(-1), IsVehicleLocation = true, VehiclePlate = plate });
                }
                db.LocationPings.Add(new LocationPing { TenantId = Tenant, UserId = Vehicle, Latitude = 40, Longitude = 29,
                    RecordedAt = DateTime.UtcNow, IsVehicleLocation = false });
                db.LocationPings.Add(new LocationPing { TenantId = Tenant, UserId = Vehicle, Latitude = 40, Longitude = 29,
                    RecordedAt = DateTime.UtcNow, IsVehicleLocation = true, VehiclePlate = "OLD PLATE" });
                if (grant)
                {
                    db.VehicleObserverAccesses.Add(new VehicleObserverAccess { TenantId = Tenant, UserId = Observer, IsEnabled = true, AllVehicles = all });
                    db.VehicleObserverVehicles.Add(new VehicleObserverVehicle { TenantId = Tenant, ObserverId = Observer, VehicleUserId = Vehicle });
                }
                await db.SaveChangesAsync();
            }
            await app.StartAsync();
            return new Host { App = app, Client = app.GetTestClient() };
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory log, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, log, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Host.Observer.ToString()),
                new Claim(WfmClaims.TenantId, Host.Tenant.ToString()),
                new Claim(ClaimTypes.Role, Request.Headers["Test-Role"].FirstOrDefault() ?? Roles.VehicleObserver) };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), "test")));
        }
    }
}

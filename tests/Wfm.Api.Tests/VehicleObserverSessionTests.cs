using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Tests;

public class VehicleObserverSessionTests
{
    [Fact]
    public async Task Refresh_keeps_one_login_record_and_cannot_extend_absolute_duration()
    {
        await using var test = await TestContext.CreateAsync();
        var auth = await test.Tokens.IssueAsync(test.User);
        var row = await test.Db.VehicleObserverSessions.SingleAsync();
        Assert.InRange((row.ExpiresAt - row.CreatedAt).TotalMinutes, 29.9, 30.1);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        Assert.Equal(row.Id.ToString(), jwt.Claims.Single(c => c.Type == VehicleObserverSessionService.ClaimName).Value);
        var fresh = await test.Tokens.RefreshAsync(auth.RefreshToken);
        Assert.NotNull(fresh);
        Assert.Single(await test.Db.VehicleObserverSessions.ToListAsync());
        Assert.All(await test.Db.RefreshTokens.ToListAsync(), r => Assert.Equal(row.Id, r.VehicleObserverSessionId));
        Assert.All(await test.Db.RefreshTokens.ToListAsync(), r => Assert.Equal(row.ExpiresAt, r.ExpiresAt));
        // A refresh raced with a browser disconnect: the original token still closes this same session.
        await test.Tokens.RevokeAsync(auth.RefreshToken, connectionClosed: true);
        Assert.Equal("ConnectionClosed", row.EndReason);
        Assert.NotNull(row.EndedAt);
        Assert.Null(await test.Tokens.RefreshAsync(fresh!.RefreshToken));
        Assert.Null(await test.Sessions.FindActiveAsync(row.Id, row.TenantId, row.UserId));
    }

    [Fact]
    public async Task Explicit_logout_is_idempotent_and_expired_sessions_are_rejected()
    {
        await using var test = await TestContext.CreateAsync();
        var auth = await test.Tokens.IssueAsync(test.User);
        var row = await test.Db.VehicleObserverSessions.SingleAsync();
        Assert.Null(await test.Sessions.FindActiveAsync(row.Id, Guid.NewGuid(), row.UserId));
        await test.Tokens.RevokeAsync(auth.RefreshToken);
        var end = row.EndedAt;
        await test.Tokens.RevokeAsync(auth.RefreshToken, connectionClosed: true);
        Assert.Equal(end, row.EndedAt);
        Assert.Equal("Logout", row.EndReason);

        var second = await test.Tokens.IssueAsync(test.User);
        var next = await test.Db.VehicleObserverSessions.SingleAsync(s => s.Id != row.Id);
        next.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        Assert.Null(await test.Sessions.FindActiveAsync(next.Id, next.TenantId, next.UserId));
        Assert.Equal("Expired", next.EndReason);
        Assert.Equal(next.ExpiresAt, next.EndedAt);
        Assert.Null(await test.Tokens.RefreshAsync(second.RefreshToken));
    }

    [Fact]
    public async Task Configured_duration_applies_to_new_logins_and_other_roles_are_unchanged()
    {
        await using var test = await TestContext.CreateAsync();
        var access = await test.Db.VehicleObserverAccesses.SingleAsync();
        access.SessionDurationMinutes = 5;
        await test.Db.SaveChangesAsync();
        await test.Tokens.IssueAsync(test.User);
        var session = await test.Db.VehicleObserverSessions.SingleAsync();
        Assert.InRange((session.ExpiresAt - session.CreatedAt).TotalMinutes, 4.9, 5.1);
        var users = test.Provider.GetRequiredService<UserManager<AppUser>>();
        await users.RemoveFromRoleAsync(test.User, Roles.VehicleObserver);
        var auth = await test.Tokens.IssueAsync(test.User);
        Assert.Single(await test.Db.VehicleObserverSessions.ToListAsync());
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Claims,
            c => c.Type == VehicleObserverSessionService.ClaimName);
        Assert.NotNull(await test.Tokens.RefreshAsync(auth.RefreshToken));
    }

    private sealed class TestContext(ServiceProvider provider, WfmDbContext db, AppUser user) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public WfmDbContext Db { get; } = db;
        public AppUser User { get; } = user;
        public TokenService Tokens => Provider.GetRequiredService<TokenService>();
        public VehicleObserverSessionService Sessions => Provider.GetRequiredService<VehicleObserverSessionService>();
        public static async Task<TestContext> CreateAsync()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<ITenantContext>(new TestTenant());
            services.AddDbContext<WfmDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            services.AddIdentityCore<AppUser>().AddRoles<AppRole>().AddEntityFrameworkStores<WfmDbContext>();
            services.AddSingleton(new JwtOptions { Key = "TestOnlyKeyWithAtLeastThirtyTwoBytes1234567890" });
            services.AddScoped<VehicleObserverSessionService>();
            services.AddScoped<TokenService>();
            var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<WfmDbContext>();
            var tenant = new Tenant { Name = "Test", Slug = "test" };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            var user = new AppUser { TenantId = tenant.Id, FullName = "Test Observer", Email = "test@example.invalid", UserName = "test@example.invalid" };
            var users = provider.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await users.CreateAsync(user, "Test123!")).Succeeded);
            Assert.True((await provider.GetRequiredService<RoleManager<AppRole>>().CreateAsync(new AppRole(Roles.VehicleObserver))).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.VehicleObserver)).Succeeded);
            db.VehicleObserverAccesses.Add(new VehicleObserverAccess { TenantId = tenant.Id, UserId = user.Id, IsEnabled = true, AllVehicles = true });
            await db.SaveChangesAsync();
            return new(provider, db, user);
        }
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class TestTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public Guid? UserId => null;
        public bool IsSuperAdmin => true;
    }
}

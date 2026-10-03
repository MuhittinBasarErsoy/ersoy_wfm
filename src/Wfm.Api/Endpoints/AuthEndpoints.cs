using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest req, UserManager<AppUser> users, WfmDbContext db, TokenService tokens) =>
        {
            var user = await users.FindByEmailAsync(req.Email.Trim());
            if (user is null || !user.IsActive || await users.IsLockedOutAsync(user))
                return Results.Json(new ApiError("E-posta veya parola hatalı."), statusCode: 401);

            if (!await users.CheckPasswordAsync(user, req.Password))
            {
                await users.AccessFailedAsync(user);
                return Results.Json(new ApiError("E-posta veya parola hatalı."), statusCode: 401);
            }
            await users.ResetAccessFailedCountAsync(user);

            if (!await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive))
                return Results.Json(new ApiError("Şirket hesabı pasif."), statusCode: 403);

            return Results.Ok(await tokens.IssueAsync(user));
        }).AllowAnonymous();

        g.MapPost("/refresh", async (RefreshRequest req, TokenService tokens) =>
            await tokens.RefreshAsync(req.RefreshToken) is { } res
                ? Results.Ok(res)
                : Results.Json(new ApiError("Oturum süresi doldu."), statusCode: 401)).AllowAnonymous();

        g.MapPost("/logout", async (RefreshRequest req, TokenService tokens) =>
        {
            await tokens.RevokeAsync(req.RefreshToken);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (HttpContext ctx, UserManager<AppUser> users, WfmDbContext db) =>
        {
            var user = await users.FindByIdAsync(ctx.User.UserId().ToString());
            if (user is null) return Results.NotFound();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId);
            return Results.Ok(Mapping.ToDto(user, await users.GetRolesAsync(user), tenant.Name));
        }).RequireAuthorization();

        g.MapGet("/tenant", async (HttpContext ctx, WfmDbContext db) =>
        {
            var tid = ctx.User.TenantId();
            var t = await db.Tenants.FirstAsync(x => x.Id == tid);
            return new TenantDto(t.Id, t.Name, t.Slug, t.IsActive, t.DefaultLatitude, t.DefaultLongitude);
        }).RequireAuthorization();
    }

    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants").WithTags("Tenants").RequireAuthorization(Policies.ManageTenants);

        g.MapGet("/", async (WfmDbContext db) =>
            await db.Tenants.Where(t => t.Slug != "system").OrderBy(t => t.Name)
                .Select(t => new TenantDto(t.Id, t.Name, t.Slug, t.IsActive, t.DefaultLatitude, t.DefaultLongitude))
                .ToListAsync());

        g.MapPost("/", async (CreateTenantRequest req, WfmDbContext db, UserManager<AppUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Slug))
                return Results.BadRequest(new ApiError("Şirket adı ve kısa adı zorunlu."));
            if (await db.Tenants.AnyAsync(t => t.Slug == req.Slug))
                return Results.Conflict(new ApiError("Bu kısa ad kullanımda."));

            await using var tx = await db.Database.BeginTransactionAsync();
            var tenant = new Tenant { Name = req.Name.Trim(), Slug = req.Slug.Trim().ToLowerInvariant() };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();

            var admin = new AppUser { TenantId = tenant.Id, Email = req.AdminEmail, UserName = req.AdminEmail, FullName = req.AdminFullName, EmailConfirmed = true };
            var res = await users.CreateAsync(admin, req.AdminPassword);
            if (!res.Succeeded) return Results.BadRequest(new ApiError(string.Join(" ", res.Errors.Select(e => e.Description))));
            await users.AddToRoleAsync(admin, Roles.TenantAdmin);
            await tx.CommitAsync();

            return Results.Ok(new TenantDto(tenant.Id, tenant.Name, tenant.Slug, true, tenant.DefaultLatitude, tenant.DefaultLongitude));
        });

        g.MapPut("/{id:guid}/active", async (Guid id, bool active, WfmDbContext db) =>
        {
            var n = await db.Tenants.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, active));
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }
}

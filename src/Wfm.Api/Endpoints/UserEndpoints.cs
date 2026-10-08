using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

namespace Wfm.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization();

        // Görev atamak için dispeçerler de kullanıcı listesini görebilir.
        g.MapGet("/", async (HttpContext ctx, WfmDbContext db, UserManager<AppUser> users, string? role) =>
        {
            var tid = ctx.User.TenantId();
            var tenantName = await db.Tenants.Where(t => t.Id == tid).Select(t => t.Name).FirstAsync();
            var list = await db.Users.Where(u => u.TenantId == tid).OrderBy(u => u.FullName).ToListAsync();
            var roleMap = await (from ur in db.UserRoles
                                 join r in db.Roles on ur.RoleId equals r.Id
                                 join u in db.Users on ur.UserId equals u.Id
                                 where u.TenantId == tid
                                 select new { ur.UserId, r.Name }).ToListAsync();
            var dtos = list.Select(u => Mapping.ToDto(u, roleMap.Where(x => x.UserId == u.Id).Select(x => x.Name!), tenantName));
            if (!string.IsNullOrEmpty(role)) dtos = dtos.Where(d => d.Roles.Contains(role));
            return dtos.ToList();
        }).RequireAuthorization(Policies.ViewTracking);

        g.MapPost("/", async (CreateUserRequest req, HttpContext ctx, UserManager<AppUser> users, WfmDbContext db) =>
        {
            if (!Roles.TenantAssignable.Contains(req.Role)) return Results.BadRequest(new ApiError("Geçersiz rol."));
            var tid = ctx.User.TenantId();
            if (req.TeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team))
                return Results.BadRequest(new ApiError("Ekip bulunamadı."));

            var u = new AppUser
            {
                TenantId = tid, Email = req.Email.Trim(), UserName = req.Email.Trim(), FullName = req.FullName.Trim(),
                PhoneNumber = req.Phone, TeamId = req.TeamId, EmailConfirmed = true
            };
            var res = await users.CreateAsync(u, req.Password);
            if (!res.Succeeded) return Results.BadRequest(new ApiError(string.Join(" ", res.Errors.Select(e => e.Description))));
            await users.AddToRoleAsync(u, req.Role);
            var tenantName = await db.Tenants.Where(t => t.Id == tid).Select(t => t.Name).FirstAsync();
            return Results.Ok(Mapping.ToDto(u, [req.Role], tenantName));
        }).RequireAuthorization(Policies.ManageUsers);

        g.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest req, HttpContext ctx, UserManager<AppUser> users, WfmDbContext db) =>
        {
            if (!Roles.TenantAssignable.Contains(req.Role)) return Results.BadRequest(new ApiError("Geçersiz rol."));
            var tid = ctx.User.TenantId();
            var u = await users.FindByIdAsync(id.ToString());
            if (u is null || u.TenantId != tid) return Results.NotFound();
            if (u.Id == ctx.User.UserId() && (!req.IsActive || req.Role != Roles.TenantAdmin) && ctx.User.IsInRole(Roles.TenantAdmin))
                return Results.BadRequest(new ApiError("Kendi yönetici yetkinizi kaldıramaz veya hesabınızı pasifleştiremezsiniz."));

            u.FullName = req.FullName.Trim();
            u.PhoneNumber = req.Phone;
            u.IsActive = req.IsActive;
            u.TeamId = req.TeamId;
            await users.UpdateAsync(u);

            var current = await users.GetRolesAsync(u);
            if (!current.SequenceEqual([req.Role]))
            {
                await users.RemoveFromRolesAsync(u, current);
                await users.AddToRoleAsync(u, req.Role);
            }
            if (!string.IsNullOrEmpty(req.NewPassword))
            {
                await users.RemovePasswordAsync(u);
                var res = await users.AddPasswordAsync(u, req.NewPassword);
                if (!res.Succeeded) return Results.BadRequest(new ApiError(string.Join(" ", res.Errors.Select(e => e.Description))));
            }
            if (!req.IsActive)
                await db.RefreshTokens.Where(r => r.UserId == u.Id && r.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTime.UtcNow));

            var tenantName = await db.Tenants.Where(t => t.Id == tid).Select(t => t.Name).FirstAsync();
            return Results.Ok(Mapping.ToDto(u, [req.Role], tenantName));
        }).RequireAuthorization(Policies.ManageUsers);

        var teams = app.MapGroup("/api/teams").WithTags("Teams").RequireAuthorization();
        teams.MapGet("/", async (WfmDbContext db) => await db.Teams.OrderBy(t => t.Name).Select(t => t.ToDto()).ToListAsync());
        teams.MapPost("/", async (SaveTeamRequest req, WfmDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("Ekip adı zorunlu."));
            var t = new Team { Name = req.Name.Trim(), Description = req.Description };
            db.Teams.Add(t);
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageUsers);
        teams.MapPut("/{id:guid}", async (Guid id, SaveTeamRequest req, WfmDbContext db) =>
        {
            var t = await db.Teams.FindAsync(id);
            if (t is null) return Results.NotFound();
            t.Name = req.Name.Trim();
            t.Description = req.Description;
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageUsers);
        teams.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, WfmDbContext db) =>
        {
            var t = await db.Teams.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            var tid = ctx.User.TenantId();
            await db.Users.Where(u => u.TenantId == tid && u.TeamId == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.TeamId, (Guid?)null));
            db.Teams.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageUsers);

        var types = app.MapGroup("/api/task-types").WithTags("TaskTypes").RequireAuthorization();
        types.MapGet("/", async (WfmDbContext db, bool? includeInactive) =>
            (await db.TaskTypes.Where(t => includeInactive == true || t.IsActive).OrderBy(t => t.Name).ToListAsync())
                .Select(t => t.ToDto()).ToList());
        types.MapGet("/{id:guid}", async (Guid id, WfmDbContext db) =>
            await db.TaskTypes.FirstOrDefaultAsync(t => t.Id == id) is { } t ? Results.Ok(t.ToDto()) : Results.NotFound());
        types.MapPost("/", async (SaveTaskTypeRequest req, WfmDbContext db) =>
        {
            var errors = TaskRules.ValidateTaskType(req.Name, req.Fields, req.Stages);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
            var t = new TaskType();
            Apply(t, req);
            db.TaskTypes.Add(t);
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageTaskTypes);
        types.MapPut("/{id:guid}", async (Guid id, SaveTaskTypeRequest req, WfmDbContext db) =>
        {
            var errors = TaskRules.ValidateTaskType(req.Name, req.Fields, req.Stages);
            if (errors.Count > 0) return Results.BadRequest(new ApiError(string.Join(" ", errors)));
            var t = await db.TaskTypes.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            Apply(t, req);
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageTaskTypes);
    }

    private static void Apply(TaskType t, SaveTaskTypeRequest req)
    {
        t.Name = req.Name.Trim();
        t.Description = req.Description;
        t.Icon = string.IsNullOrWhiteSpace(req.Icon) ? "assignment" : req.Icon;
        t.Color = string.IsNullOrWhiteSpace(req.Color) ? "#1976d2" : req.Color;
        t.IsActive = req.IsActive;
        t.Fields = req.Fields.Select((f, i) => { f.Order = i; return f; }).ToList();
        t.Completion = req.Completion;
        t.Stages = (req.Stages ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        t.RequiresVisit = req.RequiresVisit;
    }
}

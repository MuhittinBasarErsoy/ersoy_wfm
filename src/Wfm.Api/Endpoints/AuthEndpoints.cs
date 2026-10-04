using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wfm.Application;
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

        // Parola sıfırlama talebi: e-posta altyapısı olmadığı için şirket yöneticilerine bildirim gider.
        // Kullanıcının var olup olmadığı dışarı sızmasın diye yanıt her zaman aynıdır.
        g.MapPost("/forgot-password", async (ForgotPasswordRequest req, UserManager<AppUser> users, WfmDbContext db, NotificationService notify) =>
        {
            var email = req.Email?.Trim() ?? "";
            var user = email.Length == 0 ? null : await users.FindByEmailAsync(email);
            if (user is null || !user.IsActive) return Results.NoContent();
            var since = DateTime.UtcNow.AddMinutes(-10);
            var tid = user.TenantId;
            if (await db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.TenantId == tid && n.Kind == NotificationKinds.PasswordReset &&
                    n.CreatedAt >= since && n.Body.Contains(user.Email!)))
                return Results.NoContent();
            var admins = await (from u in db.Users
                                join ur in db.UserRoles on u.Id equals ur.UserId
                                join r in db.Roles on ur.RoleId equals r.Id
                                where u.TenantId == tid && u.IsActive && r.Name == Roles.TenantAdmin && u.Id != user.Id
                                select u.Id).ToListAsync();
            foreach (var a in admins)
                await notify.NotifyAsync(tid, a, "Parola sıfırlama talebi",
                    $"{user.FullName} ({user.Email}) parolasını unuttuğunu bildirdi. Kullanıcılar sayfasından yeni parola verin.", null,
                    NotificationKinds.PasswordReset);
            return Results.NoContent();
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

        g.MapPut("/me", async (UpdateProfileRequest req, HttpContext ctx, UserManager<AppUser> users, WfmDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.FullName)) return Results.BadRequest(new ApiError("Ad soyad zorunlu."));
            var user = await users.FindByIdAsync(ctx.User.UserId().ToString());
            if (user is null) return Results.NotFound();
            user.FullName = req.FullName.Trim();
            user.PhoneNumber = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
            await users.UpdateAsync(user);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId);
            return Results.Ok(Mapping.ToDto(user, await users.GetRolesAsync(user), tenant.Name));
        }).RequireAuthorization();

        g.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext ctx, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(ctx.User.UserId().ToString());
            if (user is null) return Results.NotFound();
            var res = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
            if (!res.Succeeded)
                return Results.BadRequest(new ApiError(res.Errors.Any(e => e.Code == "PasswordMismatch")
                    ? "Mevcut parola hatalı."
                    : string.Join(" ", res.Errors.Select(e => e.Description))));
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/tenant", async (HttpContext ctx, WfmDbContext db) =>
        {
            var tid = ctx.User.TenantId();
            var t = await db.Tenants.FirstAsync(x => x.Id == tid);
            return t.ToDto();
        }).RequireAuthorization();

        g.MapPut("/tenant", async (UpdateTenantSettingsRequest req, HttpContext ctx, WfmDbContext db, IFileStorage storage) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("Şirket adı zorunlu."));
            if (req.DefaultLatitude is < -90 or > 90 || req.DefaultLongitude is < -180 or > 180)
                return Results.BadRequest(new ApiError("Geçerli bir harita merkezi seçin."));
            if (!string.IsNullOrWhiteSpace(req.BrandColor) && !System.Text.RegularExpressions.Regex.IsMatch(req.BrandColor, "^#[0-9a-fA-F]{6}$"))
                return Results.BadRequest(new ApiError("Marka rengi #rrggbb biçiminde olmalı."));
            var uploaded = req.LogoUrl?.StartsWith(PublicEndpoints.LogoPathPrefix) == true;
            if (!string.IsNullOrWhiteSpace(req.LogoUrl) && !uploaded && !(Uri.TryCreate(req.LogoUrl, UriKind.Absolute, out var u) && u.Scheme == "https"))
                return Results.BadRequest(new ApiError("Logo adresi https:// ile başlayan geçerli bir adres olmalı."));
            var tid = ctx.User.TenantId();
            var t = await db.Tenants.FirstAsync(x => x.Id == tid);
            t.Name = req.Name.Trim();
            t.DefaultLatitude = req.DefaultLatitude;
            t.DefaultLongitude = req.DefaultLongitude;
            t.BrandColor = string.IsNullOrWhiteSpace(req.BrandColor) ? null : req.BrandColor.ToLowerInvariant();
            t.LogoUrl = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl.Trim();
            if (!uploaded && t.LogoPath is { } old)
            {
                // Yüklenmiş logo yerine adres girildi ya da logo kaldırıldı: eski dosyayı sil.
                await storage.DeleteAsync(old);
                (t.LogoPath, t.LogoContentType) = (null, null);
            }
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageUsers);

        g.MapPost("/tenant/logo", async ([FromForm] IFormFile file, HttpContext ctx, WfmDbContext db, IFileStorage storage) =>
        {
            string[] allowed = ["image/png", "image/jpeg", "image/webp"];
            if (file.Length is 0 or > 1024 * 1024) return Results.BadRequest(new ApiError("Logo boş ya da 1 MB'tan büyük."));
            if (!allowed.Contains(file.ContentType)) return Results.BadRequest(new ApiError("Logo PNG, JPEG ya da WebP olmalı."));
            var tid = ctx.User.TenantId();
            var t = await db.Tenants.FirstAsync(x => x.Id == tid);
            var stamp = DateTime.UtcNow.Ticks;
            var ext = file.ContentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
            string path;
            await using (var s = file.OpenReadStream())
                path = await storage.SaveAsync(s, $"{tid:N}/brand/logo-{stamp}{ext}");
            if (t.LogoPath is { } old) await storage.DeleteAsync(old);
            (t.LogoPath, t.LogoContentType) = (path, file.ContentType);
            t.LogoUrl = $"{PublicEndpoints.LogoPathPrefix}{tid}?v={stamp}";
            await db.SaveChangesAsync();
            return Results.Ok(t.ToDto());
        }).RequireAuthorization(Policies.ManageUsers).DisableAntiforgery();
    }

    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants").WithTags("Tenants").RequireAuthorization(Policies.ManageTenants);

        g.MapGet("/", async (WfmDbContext db) =>
            await db.Tenants.Where(t => t.Slug != "system").OrderBy(t => t.Name)
                .Select(t => new TenantDto(t.Id, t.Name, t.Slug, t.IsActive, t.DefaultLatitude, t.DefaultLongitude, t.BrandColor, t.LogoUrl))
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

            return Results.Ok(tenant.ToDto());
        });

        g.MapPut("/{id:guid}/active", async (Guid id, bool active, WfmDbContext db) =>
        {
            var n = await db.Tenants.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, active));
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }
}

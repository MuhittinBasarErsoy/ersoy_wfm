using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Wfm.Api.Endpoints;
using Wfm.Api.Hubs;
using Wfm.Api.Services;
using Wfm.Application;
using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Common;
using Wfm.Infrastructure;
using Wfm.Infrastructure.Data;
using Wfm.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- Altyapı ----------
var uploadRoot = Path.Combine(builder.Environment.ContentRootPath, config["Storage:UploadPath"] ?? "uploads");
builder.Services.AddInfrastructure(config.GetConnectionString("Wfm")
    ?? throw new InvalidOperationException("ConnectionStrings:Wfm tanımlı değil."), uploadRoot);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// ---------- Kimlik doğrulama ----------
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32) throw new InvalidOperationException("Jwt:Key en az 32 karakter olmalı.");
builder.Services.AddSingleton(jwt);
builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton<FileUrlSigner>();
builder.Services.AddScoped<UserDirectory>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHostedService<LocationCleanupService>();
builder.Services.AddHostedService<OverdueMonitorService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = jwt.SigningKey,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier,
        RoleClaimType = System.Security.Claims.ClaimTypes.Role
    };
    // SignalR WebSocket bağlantıları token'ı sorgu dizesinde gönderir.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = token;
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization(o =>
{
    foreach (var (policy, roles) in Policies.Map)
        o.AddPolicy(policy, p => p.RequireRole(roles));
});

builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// Geliştirme dışında sistem yöneticisi demo parolasıyla oluşturulmasın.
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(config["Seed:AdminPassword"]))
    throw new InvalidOperationException("Seed:AdminPassword tanımlı değil.");

var app = builder.Build();

// ---------- Hata eşleme ----------
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    (int status, string message) = ex switch
    {
        DomainException d => (400, d.Message),
        DbUpdateConcurrencyException => (409, "Kayıt başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin."),
        BadHttpRequestException b => (400, b.Message),
        _ => (500, "Beklenmeyen bir hata oluştu.")
    };
    if (status == 500) app.Logger.LogError(ex, "İşlenmeyen hata");
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new ApiError(message));
}));

if (app.Environment.IsDevelopment()) app.MapOpenApi();

if (app.Environment.IsDevelopment() && config.GetValue<bool>("Debug:LogRequests"))
    app.Use(async (ctx, next) => { app.Logger.LogInformation("{Method} {Path}{Query}", ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString); await next(); });

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "WFM API").AllowAnonymous();
app.MapAuthEndpoints();
app.MapTenantEndpoints();
app.MapUserEndpoints();
app.MapTaskEndpoints();
app.MapFieldEndpoints();
app.MapReportEndpoints();
app.MapPublicEndpoints();
app.MapHub<TrackingHub>(HubPaths.Tracking);
app.MapHub<NotificationHub>(HubPaths.Notifications);

// ---------- Veritabanı ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db,
        scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
        scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>(),
        config.GetValue<bool>("Seed:DemoData"),
        config["Seed:AdminPassword"]);
}

app.Run();

public partial class Program;

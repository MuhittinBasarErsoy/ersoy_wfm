using Wfm.Client;
using Wfm.Shared.UI.Services;
using Wfm.Web;
using Wfm.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Ortak istemci ve UI servisleri (her tarayıcı bağlantısı için ayrı oturum).
var apiBase = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5211";
builder.Services.AddWfmClient(apiBase, singletonSession: false);
builder.Services.AddWfmUi(singleton: false);
builder.Services.AddScoped<ITokenStore, BrowserTokenStore>();
builder.Services.AddScoped<IFieldService, OnlineFieldService>();
builder.Services.AddSingleton<IPlatformInfo, WebPlatformInfo>();
builder.Services.AddScoped<BrowserPlatform>();
builder.Services.AddScoped<IGeolocationService>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<ICameraService>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<ILocationTracker>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<IExternalActions>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<INotificationPresenter>(sp => sp.GetRequiredService<BrowserPlatform>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/not-found", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Wfm.Shared.UI.Routes).Assembly);

app.Run();

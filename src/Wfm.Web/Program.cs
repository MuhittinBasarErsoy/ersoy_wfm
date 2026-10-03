using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Wfm.Client;
using Wfm.Shared.UI.Services;
using Wfm.Web;
using Wfm.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Ortak istemci ve UI servisleri (her tarayıcı bağlantısı için ayrı oturum).
var apiBase = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5211";
// Sunucu API'ye iç adresten gider; tarayıcıdaki dosya bağlantıları için dış adres ayrıca verilebilir.
builder.Services.AddWfmClient(apiBase, singletonSession: false, builder.Configuration["Api:PublicUrl"]);

// Oturumlar Data Protection ile şifreleniyor; anahtarlar yeniden başlatmada kaybolmasın.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("Wfm.Web");

// Ters vekil (tailscale serve/funnel) arkasında gerçek şema ve istemci IP'si.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddWfmUi(singleton: false);
builder.Services.AddScoped<ITokenStore, BrowserTokenStore>();
builder.Services.AddScoped<IFieldService, OnlineFieldService>();
builder.Services.AddSingleton<IPlatformInfo, WebPlatformInfo>();
builder.Services.AddScoped<BrowserPlatform>();
builder.Services.AddScoped<IGeolocationService>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<ICameraService>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<ILocationTracker, BrowserLocationTracker>();
builder.Services.AddScoped<IExternalActions>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<INotificationPresenter>(sp => sp.GetRequiredService<BrowserPlatform>());

var app = builder.Build();

app.UseForwardedHeaders();

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

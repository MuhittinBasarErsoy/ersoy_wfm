using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wfm.Api.Services;
using Wfm.Application.Contracts;
using Wfm.Client;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Tests;

/// <summary>Push açık API: Apple yerine sahte sunucu, test için üretilmiş .p8 anahtarı.</summary>
public class ApnsFixture : ApiFixture
{
    public const string KeyId = "ABC123DEFG";
    public const string TeamId = "TEAM123456";
    public const string BundleId = "com.wfm.saha";

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public FakeApns Apple { get; } = new();
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"apns-test-{Guid.NewGuid():N}.p8");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        File.WriteAllText(_keyPath, Key.ExportPkcs8PrivateKeyPem());
        builder.UseSetting("Apns:Enabled", "true");
        builder.UseSetting("Apns:KeyId", KeyId);
        builder.UseSetting("Apns:TeamId", TeamId);
        builder.UseSetting("Apns:BundleId", BundleId);
        builder.UseSetting("Apns:KeyPath", _keyPath);
        builder.UseSetting("Apns:UseSandbox", "true");
        builder.ConfigureTestServices(s => s.AddHttpClient(ApnsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Apple));
    }
    // Anahtar dosyası bu test için üretilmiş, gerçek bir sır değildir; geçici klasörde kalabilir.
}

public sealed class FakeApns : HttpMessageHandler
{
    public record Call(Uri Uri, Version Version, Dictionary<string, string> Headers, string Body);

    private readonly List<Call> _calls = [];
    public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);

    public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_calls) _calls.Add(new Call(request.RequestUri!, request.Version, headers, body));
        return Respond();
    }

    /// <summary>Push arka planda gider; belirtilen sayıda çağrı gelene kadar bekler.</summary>
    public async Task<Call> WaitForAsync(int count)
    {
        for (var i = 0; i < 100 && Calls.Count < count; i++) await Task.Delay(50);
        Assert.True(Calls.Count >= count, $"APNs'e {count} istek bekleniyordu, {Calls.Count} geldi.");
        return Calls[count - 1];
    }
}

public class PushTests(ApnsFixture api) : IClassFixture<ApnsFixture>
{
    private const string Dispatcher = "dispecer@tamir.local";
    private const string Worker = "teknisyen1@tamir.local";
    private const string Other = "teknisyen2@tamir.local";

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private async Task<List<DeviceRegistration>> DevicesAsync(string token)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
        return await db.DeviceRegistrations.IgnoreQueryFilters().Where(d => d.Token == token).ToListAsync();
    }

    private async Task<WorkTaskDto> AssignTaskAsync(WfmApiClient dispatcher, Guid workerId)
    {
        var type = (await dispatcher.GetTaskTypesAsync()).First(t => t.Name == "Evrak İşleri").Id;
        return await dispatcher.CreateTaskAsync(new SaveWorkTaskRequest { TaskTypeId = type, Title = "Push testi", AssigneeId = workerId });
    }

    [Fact]
    public async Task Assigned_task_is_pushed_to_the_workers_iphone()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var worker = await api.LoginAs(Worker);
        var workerId = (await worker.MeAsync()).Id;
        var token = NewToken();

        var registration = await worker.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, token.ToUpperInvariant()));
        Assert.True(registration.PushEnabled);
        Assert.Equal(workerId, Assert.Single(await DevicesAsync(token)).UserId); // küçük harfe çevrilip saklanır

        var before = api.Apple.Calls.Count;
        var task = await AssignTaskAsync(dispatcher, workerId);
        var call = await api.Apple.WaitForAsync(before + 1);

        Assert.Equal($"https://api.sandbox.push.apple.com/3/device/{token}", call.Uri.ToString());
        Assert.Equal(HttpVersion.Version20, call.Version);
        Assert.Equal(ApnsFixture.BundleId, call.Headers["apns-topic"]);
        Assert.Equal("alert", call.Headers["apns-push-type"]);

        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("Yeni görev", body.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal(task.Id.ToString(), body.RootElement.GetProperty("taskId").GetString());

        // İmza: ES256, anahtar kimliği başlıkta, ekip kimliği "iss"; Apple'ın açık anahtarla doğrulayacağı biçim.
        var jwt = call.Headers["authorization"]["bearer ".Length..].Split('.');
        using var header = JsonDocument.Parse(FromBase64Url(jwt[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(ApnsFixture.KeyId, header.RootElement.GetProperty("kid").GetString());
        using var claims = JsonDocument.Parse(FromBase64Url(jwt[1]));
        Assert.Equal(ApnsFixture.TeamId, claims.RootElement.GetProperty("iss").GetString());
        Assert.True(api.Key.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), FromBase64Url(jwt[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Apple_rejected_token_is_removed_and_phone_follows_last_login()
    {
        var dispatcher = await api.LoginAs(Dispatcher);
        var worker = await api.LoginAs(Worker);
        var other = await api.LoginAs(Other);
        var otherId = (await other.MeAsync()).Id;
        var token = NewToken();

        // Aynı telefona başka biri giriş yaparsa kayıt ona geçer; eski kullanıcının bildirimleri gelmez.
        await worker.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, token));
        await other.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, token));
        Assert.Equal(otherId, Assert.Single(await DevicesAsync(token)).UserId);

        // Çıkışta yalnızca kendi kaydı silinebilir.
        await worker.UnregisterDeviceAsync(token);
        Assert.Single(await DevicesAsync(token));

        api.Apple.Respond = () => new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"reason\":\"Unregistered\"}") };
        try
        {
            var before = api.Apple.Calls.Count;
            await AssignTaskAsync(dispatcher, otherId);
            await api.Apple.WaitForAsync(before + 1);
            for (var i = 0; i < 50 && (await DevicesAsync(token)).Count > 0; i++) await Task.Delay(50);
            Assert.Empty(await DevicesAsync(token));
        }
        finally
        {
            api.Apple.Respond = () => new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Invalid_registrations_are_rejected_and_logout_removes_device()
    {
        var worker = await api.LoginAs(Worker);
        var bad = await Assert.ThrowsAsync<ApiException>(() => worker.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, "not-a-token")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        await Assert.ThrowsAsync<ApiException>(() => worker.RegisterDeviceAsync(new RegisterDeviceRequest("android", NewToken())));

        var token = NewToken();
        await worker.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, token));
        await worker.UnregisterDeviceAsync(token);
        Assert.Empty(await DevicesAsync(token));
    }

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>Varsayılan kurulumda (anahtar yok) push kapalıdır; telefon yerel bildirime devam eder.</summary>
public class PushDisabledTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Registration_reports_push_disabled_without_key()
    {
        var worker = await api.LoginAs("teknisyen1@tamir.local");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Assert.False((await worker.RegisterDeviceAsync(new RegisterDeviceRequest(DevicePlatforms.Ios, token))).PushEnabled);
    }
}

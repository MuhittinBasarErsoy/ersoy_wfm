using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wfm.Domain.Entities;
using Wfm.Infrastructure.Data;

namespace Wfm.Api.Services;

/// <summary>
/// Apple push (APNs) ayarları. <c>.p8</c> anahtarı repoya girmez; sunucuda <see cref="KeyPath"/> yolunda durur.
/// Ayar eksikse push kapalı kalır, bildirimler uygulama içinde çalışmaya devam eder.
/// </summary>
public class ApnsOptions
{
    public bool Enabled { get; set; }
    public string? KeyId { get; set; }
    public string? TeamId { get; set; }
    /// <summary>Uygulamanın Bundle ID'si (APNs "topic").</summary>
    public string? BundleId { get; set; }
    public string? KeyPath { get; set; }
    /// <summary>Xcode ile imzalanan geliştirme sürümleri Sandbox'a, TestFlight/App Store sürümleri üretime gider.</summary>
    public bool UseSandbox { get; set; } = true;
}

public record PushMessage(Guid UserId, string Title, string Body, Guid? TaskId, string Kind);

/// <summary>Bildirim kaydını push gönderiminden ayırır: Apple'a ulaşılamaması bildirimi geciktirmez.</summary>
public sealed class PushQueue
{
    private readonly Channel<PushMessage> _channel = Channel.CreateBounded<PushMessage>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(PushMessage message) => _channel.Writer.TryWrite(message);
    public ChannelReader<PushMessage> Reader => _channel.Reader;
}

public enum PushResult { Sent, InvalidToken, Failed }

/// <summary>APNs HTTP/2 istemcisi. Kimlik doğrulama .p8 anahtarıyla imzalanan kısa ömürlü ES256 JWT ile yapılır.</summary>
public sealed class ApnsClient(IHttpClientFactory http, IOptions<ApnsOptions> options, ILogger<ApnsClient> log)
{
    public const string HttpClientName = "apns";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50); // Apple 20–60 dk arasını kabul eder
    private const int MaxBodyLength = 1000;

    private readonly ApnsOptions _o = options.Value;
    private readonly Lock _gate = new();
    private ECDsa? _key;
    private bool? _enabled;
    private string? _jwt;
    private DateTime _jwtCreatedAt;

    public bool IsEnabled
    {
        get
        {
            lock (_gate) return _enabled ??= LoadKey();
        }
    }

    private bool LoadKey()
    {
        if (!_o.Enabled) return false;
        if (string.IsNullOrWhiteSpace(_o.KeyId) || string.IsNullOrWhiteSpace(_o.TeamId) || string.IsNullOrWhiteSpace(_o.BundleId) ||
            string.IsNullOrWhiteSpace(_o.KeyPath) || !File.Exists(_o.KeyPath))
        {
            log.LogWarning("APNs açık ama ayarlar eksik ya da anahtar dosyası yok ({KeyPath}); push kapalı.", _o.KeyPath);
            return false;
        }
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(_o.KeyPath));
            _key = key;
            log.LogInformation("APNs hazır ({Environment}, {BundleId}).", _o.UseSandbox ? "sandbox" : "production", _o.BundleId);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "APNs anahtarı okunamadı; push kapalı.");
            return false;
        }
    }

    public async Task<PushResult> SendAsync(string deviceToken, PushMessage m, CancellationToken ct)
    {
        if (!IsEnabled) return PushResult.Failed;
        var host = _o.UseSandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com";
        var body = m.Body.Length > MaxBodyLength ? m.Body[..MaxBodyLength] + "…" : m.Body;
        var payload = JsonSerializer.Serialize(new
        {
            aps = new { alert = new { title = m.Title, body }, sound = "default" },
            taskId = m.TaskId,
            kind = m.Kind
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{deviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("authorization", "bearer " + ProviderToken());
        request.Headers.TryAddWithoutValidation("apns-topic", _o.BundleId);
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", "10");

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return PushResult.Sent;

        var reason = await ReadReasonAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "DeviceTokenNotForTopic" or "Unregistered")
            return PushResult.InvalidToken;
        if (reason is "ExpiredProviderToken" or "InvalidProviderToken")
            lock (_gate) _jwt = null; // bir sonraki gönderimde yeniden imzala
        log.LogWarning("APNs reddetti: {Status} {Reason}", (int)response.StatusCode, reason);
        return PushResult.Failed;
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private string ProviderToken()
    {
        lock (_gate)
        {
            if (_jwt is not null && DateTime.UtcNow - _jwtCreatedAt < TokenLifetime) return _jwt;
            _jwtCreatedAt = DateTime.UtcNow;
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _o.KeyId }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = _o.TeamId, iat = new DateTimeOffset(_jwtCreatedAt).ToUnixTimeSeconds() }));
            // JWS, imzayı r||s (IEEE P1363) biçiminde ister; ECDsa.SignData varsayılanı budur.
            var signature = _key!.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256);
            return _jwt = $"{header}.{claims}.{Base64Url(signature)}";
        }
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Kuyruktaki bildirimleri kullanıcının iPhone'larına gönderir; Apple'ın geçersiz dediği cihaz kayıtlarını siler.</summary>
public sealed class PushDispatcher(PushQueue queue, ApnsClient apns, IServiceScopeFactory scopes, ILogger<PushDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(ct))
        {
            if (!apns.IsEnabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WfmDbContext>();
                // Arka planda istek bağlamı yok; kiracı filtresi yerine kullanıcı kimliği açıkça kullanılır.
                var devices = await db.DeviceRegistrations.IgnoreQueryFilters()
                    .Where(d => d.UserId == message.UserId && d.Platform == DevicePlatforms.Ios).ToListAsync(ct);
                foreach (var device in devices)
                    if (await apns.SendAsync(device.Token, message, ct) == PushResult.InvalidToken)
                        db.DeviceRegistrations.Remove(device);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Push gönderilemedi.");
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Wfm.Api.Services;

/// <summary>
/// Ekler için kısa ömürlü imzalı URL üretir; böylece &lt;img src&gt; gibi Authorization başlığı gönderemeyen
/// yerlerde de dosyalar güvenle gösterilebilir.
/// </summary>
public class FileUrlSigner(JwtOptions options)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public string CreateUrl(Guid attachmentId)
    {
        var exp = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return $"/files/{attachmentId}?exp={exp}&sig={Sign(attachmentId, exp)}";
    }

    public bool Validate(Guid attachmentId, long exp, string sig) =>
        exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(attachmentId, exp)), Encoding.ASCII.GetBytes(sig));

    private string Sign(Guid id, long exp)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Key), Encoding.UTF8.GetBytes($"{id:N}:{exp}"));
        return Convert.ToHexString(mac)[..32];
    }
}

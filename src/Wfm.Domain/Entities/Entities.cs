using Wfm.Domain.Common;
using Wfm.Domain.Enums;

namespace Wfm.Domain.Entities;

public class Tenant : Entity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Haritanın varsayılan merkezi.</summary>
    public double DefaultLatitude { get; set; } = 41.0082;
    public double DefaultLongitude { get; set; } = 28.9784;
    /// <summary>Konum ping'lerinin saklanma süresi (gün).</summary>
    public int LocationRetentionDays { get; set; } = 30;
}

public class Team : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>
/// Görev tipi: sistemin generic yapısının kalbi. Her şirket kendi görev tiplerini (çiçek teslimatı, arıza onarımı...)
/// dinamik alanlarıyla ve tamamlama gereksinimleriyle tanımlar.
/// </summary>
public class TaskType : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "assignment";
    public string Color { get; set; } = "#1976d2";
    public bool IsActive { get; set; } = true;
    public List<FieldDefinition> Fields { get; set; } = [];
    public CompletionRequirements Completion { get; set; } = new();
}

public class FieldDefinition
{
    /// <summary>Makine adı (ör. "recipient_name"). CustomFieldValues sözlüğünde anahtar olarak kullanılır.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public bool Required { get; set; }
    /// <summary>Alanı kim dolduruyor: false = görev oluşturulurken (dispeçer), true = tamamlanırken (saha çalışanı).</summary>
    public bool FilledOnCompletion { get; set; }
    public List<string> Options { get; set; } = [];
    public string? Placeholder { get; set; }
    public int Order { get; set; }
}

public class CompletionRequirements
{
    public bool RequireSignature { get; set; }
    public bool RequirePhoto { get; set; }
    public int MinPhotoCount { get; set; }
    public bool RequireNote { get; set; }
    /// <summary>Tamamlarken çalışanın görev konumuna en fazla kaç metre uzakta olabileceği (0 = kontrol yok).</summary>
    public int MaxDistanceMeters { get; set; }
}

public class TaskEvent : TenantEntity
{
    public Guid WorkTaskId { get; set; }
    public Guid UserId { get; set; }
    public WorkTaskStatus FromStatus { get; set; }
    public WorkTaskStatus ToStatus { get; set; }
    public string? Note { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public class TaskAttachment : TenantEntity
{
    public Guid WorkTaskId { get; set; }
    public Guid UploadedById { get; set; }
    public AttachmentKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Cihazda çekildiği an (offline yüklemelerde CreatedAt'ten farklı olabilir).</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Offline tekrar gönderimlerde çift kaydı önlemek için istemcinin ürettiği kimlik.</summary>
    public Guid? ClientId { get; set; }
}

public class LocationPing : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Accuracy { get; set; }
    public double? Speed { get; set; }
    public double? Heading { get; set; }
    public int? BatteryLevel { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public class Notification : TenantEntity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? WorkTaskId { get; set; }
    public DateTime? ReadAt { get; set; }
}

/// <summary>Saha çalışanının mesai oturumu. Konum takibi yalnızca açık mesai süresince yapılır.</summary>
public class WorkShift : TenantEntity
{
    public Guid UserId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}

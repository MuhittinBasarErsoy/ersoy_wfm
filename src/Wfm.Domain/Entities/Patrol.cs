using Wfm.Domain.Common;
using Wfm.Domain.Enums;

namespace Wfm.Domain.Entities;

/// <summary>
/// Devriye rotası (bekçi turu şablonu): belirli günlerde, belirli saat aralıklarında dolaşılması gereken kontrol noktaları.
/// Arka plan servisi her pencere için bir <see cref="PatrolRound"/> üretir; çalışan her noktada "Noktadayım" tuşuna basar.
/// </summary>
public class PatrolRoute : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid AssigneeId { get; set; }
    public Guid CreatedById { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>true = noktalar sırayla (1 → 2 → 3) basılmalı; false = pencere içinde her noktaya uğramak yeterli.</summary>
    public bool EnforceOrder { get; set; }
    /// <summary>Turun yapılacağı günler (boş = her gün). Gece yarısını aşan pencerede başlangıç günü esas alınır.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    /// <summary>Yerel saatle tur pencereleri (ör. 09:00–11:00, 22:00–23:00). Bitiş ≤ başlangıç ise ertesi güne taşar.</summary>
    public List<PatrolWindow> Windows { get; set; } = [];
    /// <summary>Pencere bitimine kaç dakika kala eksik noktalar için hatırlatma gönderileceği (0 = gönderme).</summary>
    public int ReminderMinutesBeforeEnd { get; set; } = 15;
    public List<PatrolCheckpoint> Checkpoints { get; set; } = [];

    public bool RunsOn(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);
}

public class PatrolWindow
{
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}

public class PatrolCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Tuşa basıldığında çalışanın noktaya en fazla kaç metre uzakta olabileceği.</summary>
    public int RadiusMeters { get; set; } = 50;
}

/// <summary>
/// Bir rotanın tek bir penceredeki turu. Noktalar üretim anında kopyalanır; rota sonradan değişse de geçmiş turlar
/// ve raporlar o günkü noktalarla kalır.
/// </summary>
public class PatrolRound : TenantEntity
{
    public Guid RouteId { get; set; }
    public string RouteName { get; set; } = "";
    public Guid AssigneeId { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public bool EnforceOrder { get; set; }
    public PatrolRoundStatus Status { get; set; } = PatrolRoundStatus.Scheduled;
    public List<PatrolCheckpoint> Checkpoints { get; set; } = [];
    public bool StartReminderSent { get; set; }
    public bool EndReminderSent { get; set; }
    public DateTime? CompletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<PatrolVisit> Visits { get; set; } = [];

    public bool IsClosed => Status is PatrolRoundStatus.Completed or PatrolRoundStatus.Incomplete or PatrolRoundStatus.Missed;

    public int MissingCount => Checkpoints.Count(c => Visits.All(v => v.CheckpointId != c.Id));

    /// <summary>
    /// Noktaya basışı doğrular ve kaydeder. <paramref name="at"/> cihazdaki basış anıdır (offline gönderimlerde geç gelebilir).
    /// Tüm noktalar tamamlanınca tur kapanır; pencere bittikten sonra gelen geçerli basış eksik turu tamamlayabilir.
    /// </summary>
    public PatrolVisit RecordVisit(Guid checkpointId, Guid userId, double lat, double lng, double? accuracy, DateTime at, Guid? clientId)
    {
        var cp = Checkpoints.FirstOrDefault(c => c.Id == checkpointId) ?? throw new DomainException("Kontrol noktası bu turda yok.");
        if (Visits.Any(v => v.CheckpointId == checkpointId)) throw new DomainException($"'{cp.Name}' noktası zaten işaretlendi.");
        if (at < WindowStart) throw new DomainException("Devriye henüz başlamadı.");
        if (at > WindowEnd) throw new DomainException("Devriye süresi doldu.");
        if (EnforceOrder && Checkpoints.Where(c => c.Order < cp.Order).FirstOrDefault(c => Visits.All(v => v.CheckpointId != c.Id)) is { } skipped)
            throw new DomainException($"Önce '{skipped.Name}' noktasına uğramalısınız.");

        var distance = Geo.DistanceMeters(lat, lng, cp.Latitude, cp.Longitude);
        if (distance > cp.RadiusMeters)
            throw new DomainException($"'{cp.Name}' noktasına {Math.Round(distance)} m uzaktasınız, en fazla {cp.RadiusMeters} m olabilir.");

        var visit = new PatrolVisit
        {
            TenantId = TenantId, RoundId = Id, CheckpointId = checkpointId, UserId = userId, VisitedAt = at,
            Latitude = lat, Longitude = lng, Accuracy = accuracy, DistanceMeters = Math.Round(distance, 1), ClientId = clientId
        };
        Visits.Add(visit);
        if (Status == PatrolRoundStatus.Scheduled) Status = PatrolRoundStatus.InProgress;
        if (MissingCount == 0)
        {
            Status = PatrolRoundStatus.Completed;
            CompletedAt = at;
        }
        UpdatedAt = DateTime.UtcNow;
        return visit;
    }

    /// <summary>Pencere bitince tamamlanmamış turu kapatır: hiç basılmadıysa Missed, kısmen yapıldıysa Incomplete.</summary>
    public bool CloseIfExpired(DateTime now)
    {
        if (IsClosed || now < WindowEnd) return false;
        Status = Visits.Count == 0 ? PatrolRoundStatus.Missed : PatrolRoundStatus.Incomplete;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>Çalışanın bir kontrol noktasında "Noktadayım" tuşuna basması.</summary>
public class PatrolVisit : TenantEntity
{
    public Guid RoundId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid UserId { get; set; }
    public DateTime VisitedAt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Accuracy { get; set; }
    public double DistanceMeters { get; set; }
    /// <summary>Offline tekrar gönderimlerde çift kaydı önlemek için istemcinin ürettiği kimlik.</summary>
    public Guid? ClientId { get; set; }
}

/// <summary>Devriye pencerelerini yerel saatten UTC'ye çevirir.</summary>
public static class PatrolSchedule
{
    /// <summary>Pencereler şirketin yerel saatindedir (sunucular Europe/Istanbul).</summary>
    public static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
    }

    public static DateOnly LocalToday(DateTime utcNow) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, Zone));

    /// <summary>Rotanın verilen yerel gündeki pencereleri (UTC). Gün rotanın günlerinden değilse boş döner.</summary>
    public static IEnumerable<(DateTime Start, DateTime End)> WindowsOn(PatrolRoute route, DateOnly day)
    {
        if (!route.RunsOn(day.DayOfWeek)) yield break;
        foreach (var w in route.Windows)
        {
            var start = day.ToDateTime(w.Start);
            var end = (w.End <= w.Start ? day.AddDays(1) : day).ToDateTime(w.End);
            yield return (ToUtc(start), ToUtc(end));
        }
    }

    private static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
}

using Wfm.Domain.Common;
using Wfm.Domain.Enums;

namespace Wfm.Domain.Entities;

public class WorkTask : TenantEntity
{
    public Guid TaskTypeId { get; set; }
    public TaskType? TaskType { get; set; }

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Draft;

    public Guid? AssigneeId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid CreatedById { get; set; }

    public string Address { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }

    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? CompletionNote { get; set; }
    public Dictionary<string, string?> CustomFieldValues { get; set; } = [];

    public byte[] RowVersion { get; set; } = [];

    /// <summary>Müşteriye gönderilen herkese açık takip linkinin anahtarı (oluşturulmadıysa null).</summary>
    public string? TrackingToken { get; set; }
    /// <summary>Gecikme bildiriminin gönderildiği an; aynı görev için tekrar bildirim gitmesin diye.</summary>
    public DateTime? OverdueNotifiedAt { get; set; }

    public List<TaskEvent> Events { get; set; } = [];
    public List<TaskAttachment> Attachments { get; set; } = [];

    public bool IsOpen => Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Failed);

    /// <summary>Planlanan bitiş zamanı geçtiği hâlde açık olan görev.</summary>
    public bool IsOverdue(DateTime utcNow) => IsOpen && ScheduledEnd is { } end && end < utcNow;

    /// <summary>Durum geçişini doğrular ve uygular. Geçersiz geçişte DomainException fırlatır.</summary>
    public TaskEvent ChangeStatus(WorkTaskStatus next, Guid userId, string? note = null, double? lat = null, double? lng = null)
    {
        if (!TaskStatusFlow.CanTransition(Status, next))
            throw new DomainException($"'{Status}' durumundan '{next}' durumuna geçilemez.");

        if (next == WorkTaskStatus.Assigned && AssigneeId is null)
            throw new DomainException("Görev bir çalışana atanmadan 'Assigned' durumuna geçemez.");

        var previous = Status;
        Status = next;
        UpdatedAt = DateTime.UtcNow;
        if (next == WorkTaskStatus.EnRoute && StartedAt is null) StartedAt = UpdatedAt;
        if (next is WorkTaskStatus.Completed or WorkTaskStatus.Failed) CompletedAt = UpdatedAt;

        return AddEvent(previous, next, userId, note, lat, lng);
    }

    /// <summary>Görevi bir çalışana atar (null = atamayı kaldır). Yeniden atamada akış baştan başlar.</summary>
    public TaskEvent Assign(Guid? assigneeId, Guid userId)
    {
        if (!IsOpen)
            throw new DomainException("Kapanmış bir görev yeniden atanamaz.");

        var previous = Status;
        AssigneeId = assigneeId;
        Status = assigneeId is null ? WorkTaskStatus.Draft : WorkTaskStatus.Assigned;
        UpdatedAt = DateTime.UtcNow;
        return AddEvent(previous, Status, userId, assigneeId is null ? "Atama kaldırıldı" : "Görev atandı", null, null);
    }

    private TaskEvent AddEvent(WorkTaskStatus from, WorkTaskStatus to, Guid userId, string? note, double? lat, double? lng)
    {
        var ev = new TaskEvent
        {
            TenantId = TenantId,
            WorkTaskId = Id,
            UserId = userId,
            FromStatus = from,
            ToStatus = to,
            Note = note,
            Latitude = lat,
            Longitude = lng
        };
        Events.Add(ev);
        return ev;
    }
}

public static class TaskStatusFlow
{
    private static readonly Dictionary<WorkTaskStatus, WorkTaskStatus[]> Allowed = new()
    {
        [WorkTaskStatus.Draft] = [WorkTaskStatus.Assigned, WorkTaskStatus.Cancelled],
        [WorkTaskStatus.Assigned] = [WorkTaskStatus.Accepted, WorkTaskStatus.Rejected, WorkTaskStatus.Cancelled],
        [WorkTaskStatus.Accepted] = [WorkTaskStatus.EnRoute, WorkTaskStatus.Cancelled, WorkTaskStatus.Failed],
        [WorkTaskStatus.EnRoute] = [WorkTaskStatus.OnSite, WorkTaskStatus.Cancelled, WorkTaskStatus.Failed],
        [WorkTaskStatus.OnSite] = [WorkTaskStatus.Completed, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled],
        [WorkTaskStatus.Rejected] = [WorkTaskStatus.Assigned, WorkTaskStatus.Cancelled],
        [WorkTaskStatus.Completed] = [],
        [WorkTaskStatus.Cancelled] = [],
        [WorkTaskStatus.Failed] = [],
    };

    public static bool CanTransition(WorkTaskStatus from, WorkTaskStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static IReadOnlyList<WorkTaskStatus> NextStatuses(WorkTaskStatus from) =>
        Allowed.TryGetValue(from, out var next) ? next : [];

    /// <summary>Saha çalışanının kendi görevinde yapabileceği geçişler.</summary>
    public static bool IsFieldWorkerTransition(WorkTaskStatus to) =>
        to is WorkTaskStatus.Accepted or WorkTaskStatus.Rejected or WorkTaskStatus.EnRoute
            or WorkTaskStatus.OnSite or WorkTaskStatus.Completed or WorkTaskStatus.Failed;

    public static string DisplayName(WorkTaskStatus s) => s switch
    {
        WorkTaskStatus.Draft => "Taslak",
        WorkTaskStatus.Assigned => "Atandı",
        WorkTaskStatus.Accepted => "Kabul edildi",
        WorkTaskStatus.EnRoute => "Yolda",
        WorkTaskStatus.OnSite => "Yerinde",
        WorkTaskStatus.Completed => "Tamamlandı",
        WorkTaskStatus.Rejected => "Reddedildi",
        WorkTaskStatus.Cancelled => "İptal",
        WorkTaskStatus.Failed => "Başarısız",
        _ => s.ToString()
    };

    /// <summary>Saha çalışanı için buton metni.</summary>
    public static string ActionName(WorkTaskStatus to) => to switch
    {
        WorkTaskStatus.Accepted => "Kabul et",
        WorkTaskStatus.Rejected => "Reddet",
        WorkTaskStatus.EnRoute => "Yola çıktım",
        WorkTaskStatus.OnSite => "Vardım",
        WorkTaskStatus.Completed => "Tamamla",
        WorkTaskStatus.Failed => "Yapılamadı",
        WorkTaskStatus.Cancelled => "İptal et",
        WorkTaskStatus.Assigned => "Ata",
        _ => DisplayName(to)
    };
}

public static class Geo
{
    /// <summary>İki koordinat arası mesafe (metre, Haversine).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

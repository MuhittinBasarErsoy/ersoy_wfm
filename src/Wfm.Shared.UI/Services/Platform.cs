using Wfm.Application.Contracts;
using Wfm.Domain.Enums;

namespace Wfm.Shared.UI.Services;

public record GeoPoint(double Latitude, double Longitude, double? Accuracy = null);
public record CapturedFile(byte[] Data, string FileName, string ContentType);
public record AddressResult(string Address, double Latitude, double Longitude);

/// <summary>Uygulamanın çalıştığı platform (web dashboard / mobil uygulama).</summary>
public interface IPlatformInfo
{
    bool IsMobile { get; }
}

public interface IGeolocationService
{
    Task<GeoPoint?> GetCurrentAsync();
}

public interface ICameraService
{
    bool IsSupported { get; }
    Task<CapturedFile?> CapturePhotoAsync();
}

/// <summary>Arka planda periyodik konum gönderimi (yalnızca mobilde gerçek implementasyonu vardır).</summary>
public interface ILocationTracker
{
    bool IsSupported { get; }
    bool IsRunning { get; }
    Task<bool> StartAsync();
    Task StopAsync();
}

/// <summary>Uygulama dışı aksiyonlar: yol tarifi, arama.</summary>
public interface IExternalActions
{
    Task OpenDirectionsAsync(double lat, double lng, string label);
    Task CallAsync(string phone);
}

/// <summary>Sistem bildirimi gösterimi (mobilde yerel bildirim; web'de uygulama içi snackbar yeterli).</summary>
public interface INotificationPresenter
{
    Task ShowAsync(string title, string body, Guid? taskId);
}

/// <summary>
/// Saha çalışanı ekranlarının veri kaynağı. Web'de doğrudan API'ye gider; mobilde yerel SQLite önbelleği + outbox kuyruğu
/// ile offline çalışır ve bağlantı gelince senkronize eder.
/// </summary>
public interface IFieldService
{
    event Action? Changed;
    bool IsOnline { get; }
    int PendingCount { get; }
    DateTime? LastSync { get; }
    string? LastError { get; }

    Task<IReadOnlyList<WorkTaskDetailDto>> GetTasksAsync();
    Task<WorkTaskDetailDto?> GetTaskAsync(Guid id);
    Task<TaskTypeDto?> GetTaskTypeAsync(Guid id);
    Task SyncAsync();

    Task ChangeStatusAsync(Guid taskId, ChangeStatusRequest request);
    Task ChangeStageAsync(Guid taskId, ChangeStageRequest request);
    Task AddAttachmentAsync(Guid taskId, CapturedFile file, AttachmentKind kind, GeoPoint? location);
    Task RemoveAttachmentAsync(Guid taskId, Guid attachmentId);

    /// <summary>Çalışanın açık ve yakın zamandaki devriye turları.</summary>
    Task<IReadOnlyList<PatrolRoundDto>> GetPatrolRoundsAsync();
    /// <summary>Kontrol noktasına basışı gönderir (mobilde çevrimdışıysa kuyruğa alır) ve turun güncel hâlini döner.</summary>
    Task<PatrolRoundDto?> RecordPatrolVisitAsync(Guid roundId, PatrolVisitRequest request);

    Task<ShiftDto?> GetShiftAsync();
    Task<ShiftDto?> SetShiftAsync(bool onShift);

    /// <summary>SignalR'dan gelen görev değişikliğini yerel önbelleğe yansıtır.</summary>
    Task OnServerTaskChangedAsync(WorkTaskDto task);
    Task ClearAsync();
}

/// <summary>Devriye basışının cihazda ön kontrolü (sunucu aynı kuralları yeniden uygular).</summary>
public static class PatrolRules
{
    public static double Distance(PatrolRoundCheckpointDto c, GeoPoint me) =>
        Wfm.Domain.Entities.Geo.DistanceMeters(me.Latitude, me.Longitude, c.Latitude, c.Longitude);

    /// <summary>Basılamıyorsa nedeni; basılabiliyorsa null.</summary>
    public static string? Problem(PatrolRoundDto round, PatrolRoundCheckpointDto c, GeoPoint? me)
    {
        if (c.VisitedAt is not null) return "İşaretlendi";
        if (DateTime.UtcNow < round.WindowStart) return $"Başlangıç {Ui.Local(round.WindowStart, "HH:mm")}";
        if (DateTime.UtcNow >= round.WindowEnd) return "Süre doldu";
        if (round.NextInOrder is { } next && next.Id != c.Id) return $"Önce '{next.Name}'";
        if (me is null) return "Konum bekleniyor";
        var d = Distance(c, me);
        return d > c.RadiusMeters ? $"{Ui.Distance(d)} uzaktasınız" : null;
    }
}

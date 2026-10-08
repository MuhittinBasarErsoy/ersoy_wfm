using System.Net;
using Wfm.Application.Contracts;
using Wfm.Shared.UI.Services;

namespace Wfm.Shared.UI.Components;

/// <summary>Harita işaretçisi. <see cref="Popup"/> HTML'dir; içine giren kullanıcı verisi mutlaka encode edilmelidir.</summary>
public record MapMarker(string Id, double Lat, double Lng, string Color, string? Label = null, string? Title = null,
    string? Popup = null, bool Pulse = false)
{
    public const string WorkerOnShift = "#00897b";
    public const string WorkerStale = "#ffa000";
    public const string WorkerOffShift = "#9e9e9e";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static bool IsStale(WorkerLocationDto w) => DateTime.UtcNow - w.RecordedAt > StaleAfter;

    public static string WorkerColor(WorkerLocationDto w) => !w.OnShift ? WorkerOffShift : IsStale(w) ? WorkerStale : WorkerOnShift;

    public static MapMarker ForTask(WorkTaskDto t, string? link = null) => new(
        t.Id.ToString(), t.Latitude, t.Longitude, Ui.StatusHex(t.Status),
        Label: t.Priority >= Domain.Enums.TaskPriority.High ? "!" : null,
        Title: t.Title,
        Popup: $"<b>{E(t.Title)}</b><br/>{E(t.TaskTypeName)} · {E(Ui.Status(t.Status))}" +
               (t.Stage is null ? "" : $" · {E(t.Stage)}") +
               (t.IsOverdue ? " · <b style=\"color:#c62828\">Gecikti</b>" : "") +
               $"<br/>{E(t.Address)}" +
               (t.AssigneeName is null ? "<br/><i>Atanmadı</i>" : $"<br/>Atanan: {E(t.AssigneeName)}") +
               (link is null ? "" : $"<br/><a href=\"{link}\">Detay →</a>"));

    public static MapMarker ForWorker(WorkerLocationDto w)
    {
        var stale = IsStale(w);
        return new(w.UserId.ToString(), w.Latitude, w.Longitude, WorkerColor(w),
            Label: Ui.Initials(w.FullName), Title: w.FullName,
            Popup: $"<b>{E(w.FullName)}</b><br/>{(w.OnShift ? "Mesaide" : "Mesai dışı")} · son konum {E(Ui.Ago(w.RecordedAt))}" +
                   (w.BatteryLevel is { } b ? $"<br/>Pil: %{b}" : "") +
                   (w.ActiveTaskTitle is null ? "" : $"<br/>Aktif görev: {E(w.ActiveTaskTitle)}"),
            Pulse: w.OnShift && !stale);
    }
}

public record MarkerClick(string Layer, string Id);

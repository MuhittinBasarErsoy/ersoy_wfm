using System.Net;
using Wfm.Application.Contracts;
using Wfm.Shared.UI.Services;

namespace Wfm.Shared.UI.Components;

/// <summary>Harita işaretçisi. <see cref="Popup"/> HTML'dir; içine giren kullanıcı verisi mutlaka encode edilmelidir.</summary>
public record MapMarker(string Id, double Lat, double Lng, string Color, string? Label = null, string? Title = null,
    string? Popup = null, bool Pulse = false)
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static MapMarker ForTask(WorkTaskDto t, string? link = null) => new(
        t.Id.ToString(), t.Latitude, t.Longitude, Ui.StatusHex(t.Status),
        Label: t.Priority >= Domain.Enums.TaskPriority.High ? "!" : null,
        Title: t.Title,
        Popup: $"<b>{E(t.Title)}</b><br/>{E(t.TaskTypeName)} · {E(Ui.Status(t.Status))}<br/>{E(t.Address)}" +
               (t.AssigneeName is null ? "" : $"<br/>👤 {E(t.AssigneeName)}") +
               (link is null ? "" : $"<br/><a href=\"{link}\">Detay →</a>"));

    public static MapMarker ForWorker(WorkerLocationDto w)
    {
        var stale = DateTime.UtcNow - w.RecordedAt > TimeSpan.FromMinutes(15);
        return new(w.UserId.ToString(), w.Latitude, w.Longitude,
            !w.OnShift ? "#9e9e9e" : stale ? "#ffa000" : "#00897b",
            Label: Ui.Initials(w.FullName), Title: w.FullName,
            Popup: $"<b>{E(w.FullName)}</b><br/>{(w.OnShift ? "Mesaide" : "Mesai dışı")} · {E(Ui.Ago(w.RecordedAt))}" +
                   (w.BatteryLevel is { } b ? $"<br/>🔋 %{b}" : "") +
                   (w.ActiveTaskTitle is null ? "" : $"<br/>📋 {E(w.ActiveTaskTitle)}"),
            Pulse: w.OnShift && !stale);
    }
}

public record MarkerClick(string Layer, string Id);

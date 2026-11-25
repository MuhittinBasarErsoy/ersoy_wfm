using System.Text.Json;
using Wfm.Application.Contracts;

namespace Wfm.Application;

public static class VehicleAccessScheduleRules
{
    public static bool IsValid(IReadOnlyList<VehicleAccessWindow>? windows) =>
        windows is { Count: > 0 and <= 7 } &&
        windows.All(w => w is not null && Enum.IsDefined(w.Day) &&
            w.StartMinute is >= 0 and < 1440 && w.EndMinute is >= 0 and < 1440 && w.StartMinute != w.EndMinute) &&
        windows.Select(w => w.Day).Distinct().Count() == windows.Count;

    public static List<VehicleAccessWindow> Read(string? json)
    {
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<VehicleAccessWindow>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Write(List<VehicleAccessWindow> windows) => JsonSerializer.Serialize(windows.OrderBy(w => w.Day));

    public static bool Allows(string? json, DateTimeOffset now)
    {
        // Null preserves existing grants. A malformed or empty restricted schedule fails closed.
        if (json is null) return true;
        var windows = Read(json);
        if (!IsValid(windows)) return false;
        var local = now.ToOffset(TimeSpan.FromHours(3));
        var minute = local.Hour * 60 + local.Minute;
        var previousDay = (DayOfWeek)(((int)local.DayOfWeek + 6) % 7);
        return windows.Any(w => w.StartMinute < w.EndMinute
            ? w.Day == local.DayOfWeek && minute >= w.StartMinute && minute < w.EndMinute
            : w.Day == local.DayOfWeek && minute >= w.StartMinute || w.Day == previousDay && minute < w.EndMinute);
    }
}

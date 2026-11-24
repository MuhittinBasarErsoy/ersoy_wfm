using Wfm.Application.Contracts;
using Wfm.Domain;
using Wfm.Domain.Entities;

namespace Wfm.Application;

public static class VehicleTrackingRules
{
    public static string NormalizePlate(string? plate) => (plate ?? "").Trim().ToUpperInvariant();

    public static string? ValidateProfile(string role, bool enabled, string? plate, string? label)
    {
        if (!enabled) return null;
        if (role != Roles.FieldWorker) return "Araç takibi yalnızca saha çalışanlarında açılabilir.";
        if (string.IsNullOrWhiteSpace(plate) || plate.Trim().Length > 30) return "Plaka zorunlu ve en fazla 30 karakter olmalı.";
        if (label?.Trim().Length > 100) return "Araç adı en fazla 100 karakter olmalı.";
        return null;
    }

    // Speed is reported by the phone in metres/second. Never interpret an absent/invalid speed as motion.
    public static double? SpeedKmh(double? speed) => speed is { } s && double.IsFinite(s) && s >= 0 && s <= 70 ? s * 3.6 : null;

    public static VehicleHistoryDto History(IEnumerable<VehicleTrackPoint> source, bool truncated)
    {
        var points = new List<VehicleTrackPoint>();
        var stops = new List<VehicleStopDto>();
        VehicleTrackPoint? previous = null, stopStart = null, stopEnd = null;
        int segment = 0;
        double distance = 0, moving = 0;
        void FlushStop()
        {
            if (stopStart is { } a && stopEnd is { } b && b.RecordedAt - a.RecordedAt >= TimeSpan.FromMinutes(5))
                stops.Add(new(a.Latitude, a.Longitude, a.RecordedAt, b.RecordedAt));
            stopStart = stopEnd = null;
        }
        foreach (var p in source.OrderBy(p => p.RecordedAt))
        {
            if (!double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || Math.Abs(p.Latitude) > 90 ||
                Math.Abs(p.Longitude) > 180 || p.Accuracy is { } accuracy && (!double.IsFinite(accuracy) || accuracy < 0 || accuracy > 100))
            {
                FlushStop(); previous = null; segment++; continue;
            }
            if (previous is { } prev && p.RecordedAt <= prev.RecordedAt) continue;
            var elapsed = previous is { } prevTime ? (p.RecordedAt - prevTime.RecordedAt).TotalSeconds : 0;
            var metres = previous is { } prevPoint ? Geo.DistanceMeters(prevPoint.Latitude, prevPoint.Longitude, p.Latitude, p.Longitude) : 0;
            var continuous = previous is not null && elapsed <= 300 && metres / elapsed * 3.6 <= 250;
            if (!continuous) { FlushStop(); segment++; }
            if (continuous)
            {
                distance += metres / 1000;
                if ((p.SpeedKmh ?? metres / elapsed * 3.6) >= 5) moving += elapsed / 60;
            }
            var speed = p.SpeedKmh is { } v && double.IsFinite(v) && v >= 0 && v <= 252 ? p.SpeedKmh : null;
            var current = p with { Segment = segment, SpeedKmh = speed };
            points.Add(current);
            if (speed is < 5)
            {
                if (stopStart is { } anchor && Geo.DistanceMeters(anchor.Latitude, anchor.Longitude, p.Latitude, p.Longitude) > 100)
                    FlushStop();
                stopStart ??= current;
                stopEnd = current;
            }
            else FlushStop();
            previous = current;
        }
        FlushStop();
        return new(points, stops, Math.Round(distance, 2), Math.Round(moving, 1),
            points.Where(p => p.SpeedKmh.HasValue).Select(p => p.SpeedKmh).DefaultIfEmpty(null).Max(), truncated);
    }
}

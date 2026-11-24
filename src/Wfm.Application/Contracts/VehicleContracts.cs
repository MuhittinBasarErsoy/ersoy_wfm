namespace Wfm.Application.Contracts;

public record VehicleLocationDto(Guid UserId, string Plate, string? Label, string Driver,
    bool OnShift, double? Latitude, double? Longitude, double? SpeedKmh, double? Heading,
    double? Accuracy, int? BatteryLevel, DateTime? RecordedAt);

public record VehicleSnapshotDto(double CenterLatitude, double CenterLongitude, DateTime ServerTime,
    int RetentionDays, List<VehicleLocationDto> Vehicles);

public record VehicleTrackPoint(double Latitude, double Longitude, DateTime RecordedAt,
    double? SpeedKmh, double? Accuracy, int Segment = 0);

public record VehicleStopDto(double Latitude, double Longitude, DateTime From, DateTime To);

public record VehicleHistoryDto(List<VehicleTrackPoint> Points, List<VehicleStopDto> Stops,
    double ApproximateDistanceKm, double MovingMinutes, double? MaxSpeedKmh, bool Truncated);

public record SaveVehicleAccessRequest(bool IsEnabled, bool AllVehicles, DateTimeOffset? ExpiresAt,
    List<Guid> VehicleUserIds);

public record VehicleAccessDto(Guid UserId, string FullName, bool IsEnabled, bool AllVehicles,
    DateTime? ExpiresAt, List<Guid> VehicleUserIds);

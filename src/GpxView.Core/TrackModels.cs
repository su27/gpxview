namespace GpxView.Core;

public enum TrackFileFormat
{
    Gpx,
    Kml,
    Kmz,
    Fit
}

public enum SourceCoordinateSystem
{
    Wgs84,
    Gcj02,
    Bd09
}

public sealed record TrackPoint
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public double? ElevationMeters { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public double? SpeedMetersPerSecond { get; init; }
    public int? HeartRateBpm { get; init; }
    public int? CadenceRpm { get; init; }
    public double? PowerWatts { get; init; }
    public double? TemperatureCelsius { get; init; }
}

public sealed record TrackSegment
{
    public string? Name { get; init; }
    public required IReadOnlyList<TrackPoint> Points { get; init; }
}

public sealed record TrackWaypoint
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public double? ElevationMeters { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? Name { get; init; }
    public string? Comment { get; init; }
    public string? Description { get; init; }
    public string? Symbol { get; init; }
    public string? Type { get; init; }
}

public sealed record TrackDocument
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required TrackFileFormat Format { get; init; }
    public required IReadOnlyList<TrackSegment> Segments { get; init; }
    public IReadOnlyList<TrackWaypoint> Waypoints { get; init; } = [];

    public int PointCount => Segments.Sum(segment => segment.Points.Count);
    public int WaypointCount => Waypoints.Count;
}

public sealed record GeoBounds(double South, double West, double North, double East);

public sealed record TrackStatistics
{
    public required int PointCount { get; init; }
    public required int SegmentCount { get; init; }
    public required double DistanceMeters { get; init; }
    public required TimeSpan Duration { get; init; }
    public required TimeSpan MovingTime { get; init; }
    public required double ElevationGainMeters { get; init; }
    public required double ElevationLossMeters { get; init; }
    public double? MinimumElevationMeters { get; init; }
    public double? MaximumElevationMeters { get; init; }
    public double? AverageSpeedMetersPerSecond { get; init; }
    public double? MaximumSpeedMetersPerSecond { get; init; }
    public double? AverageHeartRateBpm { get; init; }
    public int? MaximumHeartRateBpm { get; init; }
    public double? AverageCadenceRpm { get; init; }
    public double? AveragePowerWatts { get; init; }
    public GeoBounds? Bounds { get; init; }
}

public sealed record TrackLoadOptions
{
    public SourceCoordinateSystem SourceCoordinateSystem { get; init; } = SourceCoordinateSystem.Wgs84;
}

public interface ITrackReader
{
    TrackFileFormat Format { get; }
    TrackDocument Read(Stream stream, string sourcePath);
}

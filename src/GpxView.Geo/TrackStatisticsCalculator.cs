using GpxView.Core;

namespace GpxView.Geo;

public static class TrackStatisticsCalculator
{
    private const double EarthRadiusMeters = 6_371_008.8;
    private const double MovingThresholdMetersPerSecond = 0.5;

    public static TrackStatistics Calculate(TrackDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var distance = 0d;
        var movingSeconds = 0d;
        var durationSeconds = 0d;
        var elevationGain = 0d;
        var elevationLoss = 0d;
        var elevations = new List<double>();
        var speeds = new List<double>();
        var heartRates = new List<int>();
        var cadences = new List<int>();
        var powers = new List<double>();
        var allPoints = document.Segments.SelectMany(segment => segment.Points).ToArray();

        foreach (var point in allPoints)
        {
            if (point.ElevationMeters is { } elevation) elevations.Add(elevation);
            if (point.SpeedMetersPerSecond is >= 0 and { } speed) speeds.Add(speed);
            if (point.HeartRateBpm is > 0 and { } heartRate) heartRates.Add(heartRate);
            if (point.CadenceRpm is >= 0 and { } cadence) cadences.Add(cadence);
            if (point.PowerWatts is >= 0 and { } power) powers.Add(power);
        }

        foreach (var segment in document.Segments)
        {
            for (var index = 1; index < segment.Points.Count; index++)
            {
                var previous = segment.Points[index - 1];
                var current = segment.Points[index];
                var stepDistance = DistanceMeters(previous, current);
                distance += stepDistance;

                if (previous.ElevationMeters is { } previousElevation && current.ElevationMeters is { } currentElevation)
                {
                    var difference = currentElevation - previousElevation;
                    // Ignore sub-metre noise which otherwise exaggerates total ascent.
                    if (difference >= 1) elevationGain += difference;
                    else if (difference <= -1) elevationLoss -= difference;
                }

                if (previous.Timestamp is not { } previousTime || current.Timestamp is not { } currentTime)
                    continue;

                var seconds = (currentTime - previousTime).TotalSeconds;
                if (seconds is <= 0 or > 3600) continue;

                durationSeconds += seconds;
                var speed = current.SpeedMetersPerSecond ?? stepDistance / seconds;
                if (speed >= MovingThresholdMetersPerSecond) movingSeconds += seconds;
            }
        }

        GeoBounds? bounds = null;
        if (allPoints.Length > 0)
        {
            bounds = new GeoBounds(
                allPoints.Min(point => point.Latitude),
                allPoints.Min(point => point.Longitude),
                allPoints.Max(point => point.Latitude),
                allPoints.Max(point => point.Longitude));
        }

        return new TrackStatistics
        {
            PointCount = allPoints.Length,
            SegmentCount = document.Segments.Count,
            DistanceMeters = distance,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            MovingTime = TimeSpan.FromSeconds(movingSeconds),
            ElevationGainMeters = elevationGain,
            ElevationLossMeters = elevationLoss,
            MinimumElevationMeters = elevations.Count == 0 ? null : elevations.Min(),
            MaximumElevationMeters = elevations.Count == 0 ? null : elevations.Max(),
            AverageSpeedMetersPerSecond = speeds.Count > 0
                ? speeds.Average()
                : durationSeconds > 0 ? distance / durationSeconds : null,
            MaximumSpeedMetersPerSecond = speeds.Count == 0 ? null : speeds.Max(),
            AverageHeartRateBpm = heartRates.Count == 0 ? null : heartRates.Average(),
            MaximumHeartRateBpm = heartRates.Count == 0 ? null : heartRates.Max(),
            AverageCadenceRpm = cadences.Count == 0 ? null : cadences.Average(),
            AveragePowerWatts = powers.Count == 0 ? null : powers.Average(),
            Bounds = bounds
        };
    }

    public static double DistanceMeters(TrackPoint first, TrackPoint second)
    {
        var latitude1 = DegreesToRadians(first.Latitude);
        var latitude2 = DegreesToRadians(second.Latitude);
        var latitudeDelta = latitude2 - latitude1;
        var longitudeDelta = DegreesToRadians(second.Longitude - first.Longitude);
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
                        + Math.Cos(latitude1) * Math.Cos(latitude2)
                        * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(haversine)));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}

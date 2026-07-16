using GpxView.Core;

namespace GpxView.Geo;

public static class CoordinateConverter
{
    private const double Pi = Math.PI;
    private const double SemiMajorAxis = 6378245.0;
    private const double EccentricitySquared = 0.00669342162296594323;

    public static TrackDocument ToWgs84(TrackDocument document, SourceCoordinateSystem source)
    {
        if (source == SourceCoordinateSystem.Wgs84) return document;

        return document with
        {
            Segments = document.Segments.Select(segment => segment with
            {
                Points = segment.Points.Select(point => ConvertPoint(point, source)).ToArray()
            }).ToArray()
        };
    }

    private static TrackPoint ConvertPoint(TrackPoint point, SourceCoordinateSystem source)
    {
        var (latitude, longitude) = source switch
        {
            SourceCoordinateSystem.Gcj02 => Gcj02ToWgs84(point.Latitude, point.Longitude),
            SourceCoordinateSystem.Bd09 => Bd09ToWgs84(point.Latitude, point.Longitude),
            _ => (point.Latitude, point.Longitude)
        };

        return point with { Latitude = latitude, Longitude = longitude };
    }

    public static (double Latitude, double Longitude) Bd09ToWgs84(double latitude, double longitude)
    {
        var x = longitude - 0.0065;
        var y = latitude - 0.006;
        var z = Math.Sqrt(x * x + y * y) - 0.00002 * Math.Sin(y * Pi * 3000 / 180);
        var theta = Math.Atan2(y, x) - 0.000003 * Math.Cos(x * Pi * 3000 / 180);
        var gcjLongitude = z * Math.Cos(theta);
        var gcjLatitude = z * Math.Sin(theta);
        return Gcj02ToWgs84(gcjLatitude, gcjLongitude);
    }

    public static (double Latitude, double Longitude) Gcj02ToWgs84(double latitude, double longitude)
    {
        if (IsOutsideMainlandChina(latitude, longitude)) return (latitude, longitude);

        // Iterative inverse is more accurate than the common one-step approximation.
        var wgsLatitude = latitude;
        var wgsLongitude = longitude;
        for (var i = 0; i < 5; i++)
        {
            var (projectedLatitude, projectedLongitude) = Wgs84ToGcj02(wgsLatitude, wgsLongitude);
            wgsLatitude -= projectedLatitude - latitude;
            wgsLongitude -= projectedLongitude - longitude;
        }

        return (wgsLatitude, wgsLongitude);
    }

    private static (double Latitude, double Longitude) Wgs84ToGcj02(double latitude, double longitude)
    {
        if (IsOutsideMainlandChina(latitude, longitude)) return (latitude, longitude);

        var latitudeDelta = TransformLatitude(longitude - 105, latitude - 35);
        var longitudeDelta = TransformLongitude(longitude - 105, latitude - 35);
        var radians = latitude / 180 * Pi;
        var magic = Math.Sin(radians);
        magic = 1 - EccentricitySquared * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        latitudeDelta = latitudeDelta * 180 / ((SemiMajorAxis * (1 - EccentricitySquared)) / (magic * sqrtMagic) * Pi);
        longitudeDelta = longitudeDelta * 180 / (SemiMajorAxis / sqrtMagic * Math.Cos(radians) * Pi);
        return (latitude + latitudeDelta, longitude + longitudeDelta);
    }

    private static bool IsOutsideMainlandChina(double latitude, double longitude) =>
        longitude is < 72.004 or > 137.8347 || latitude is < 0.8293 or > 55.8271;

    private static double TransformLatitude(double x, double y)
    {
        var value = -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        value += (20 * Math.Sin(6 * x * Pi) + 20 * Math.Sin(2 * x * Pi)) * 2 / 3;
        value += (20 * Math.Sin(y * Pi) + 40 * Math.Sin(y / 3 * Pi)) * 2 / 3;
        value += (160 * Math.Sin(y / 12 * Pi) + 320 * Math.Sin(y * Pi / 30)) * 2 / 3;
        return value;
    }

    private static double TransformLongitude(double x, double y)
    {
        var value = 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        value += (20 * Math.Sin(6 * x * Pi) + 20 * Math.Sin(2 * x * Pi)) * 2 / 3;
        value += (20 * Math.Sin(x * Pi) + 40 * Math.Sin(x / 3 * Pi)) * 2 / 3;
        value += (150 * Math.Sin(x / 12 * Pi) + 300 * Math.Sin(x / 30 * Pi)) * 2 / 3;
        return value;
    }
}

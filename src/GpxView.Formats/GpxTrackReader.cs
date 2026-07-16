using System.Globalization;
using System.Xml;
using GpxView.Core;

namespace GpxView.Formats;

public sealed class GpxTrackReader : ITrackReader
{
    public TrackFileFormat Format => TrackFileFormat.Gpx;

    public TrackDocument Read(Stream stream, string sourcePath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var reader = XmlReader.Create(stream, settings);
        var segments = new List<TrackSegment>();
        List<TrackPoint>? currentPoints = null;
        PointBuilder? currentPoint = null;
        string? documentName = null;
        string? segmentName = null;
        var isRoute = false;

        while (reader.Read())
        {
            var localName = reader.LocalName;
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (localName is "trkseg" or "rte")
                {
                    FinishSegment(segments, ref currentPoints, ref segmentName);
                    currentPoints = [];
                    isRoute = localName == "rte";
                }
                else if (localName is "trkpt" or "rtept")
                {
                    currentPoints ??= [];
                    if (TryDouble(reader.GetAttribute("lat"), out var latitude)
                        && TryDouble(reader.GetAttribute("lon"), out var longitude))
                    {
                        currentPoint = new PointBuilder(latitude, longitude);
                        if (reader.IsEmptyElement)
                        {
                            currentPoints.Add(currentPoint.Build());
                            currentPoint = null;
                        }
                    }
                }
                else if (currentPoint is not null && !reader.IsEmptyElement)
                {
                    ReadPointValue(reader, currentPoint, localName);
                }
                else if (localName == "name" && !reader.IsEmptyElement)
                {
                    var name = reader.ReadString().Trim();
                    if (currentPoints is not null && (isRoute || segments.Count >= 0)) segmentName ??= name;
                    else documentName ??= name;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (localName is "trkpt" or "rtept")
                {
                    if (currentPoint is not null) currentPoints?.Add(currentPoint.Build());
                    currentPoint = null;
                }
                else if (localName is "trkseg" or "rte")
                {
                    FinishSegment(segments, ref currentPoints, ref segmentName);
                    isRoute = false;
                }
            }
        }

        FinishSegment(segments, ref currentPoints, ref segmentName);
        return new TrackDocument
        {
            Name = string.IsNullOrWhiteSpace(documentName) ? Path.GetFileNameWithoutExtension(sourcePath) : documentName,
            SourcePath = sourcePath,
            Format = Format,
            Segments = segments
        };
    }

    private static void ReadPointValue(XmlReader reader, PointBuilder point, string localName)
    {
        if (localName is not ("ele" or "time" or "speed" or "hr" or "cad" or "atemp" or "temp" or "power"))
            return;

        var value = reader.ReadString();
        switch (localName)
        {
            case "ele" when TryDouble(value, out var elevation): point.Elevation = elevation; break;
            case "time" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time): point.Timestamp = time; break;
            case "speed" when TryDouble(value, out var speed): point.Speed = speed; break;
            case "hr" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var heartRate): point.HeartRate = heartRate; break;
            case "cad" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cadence): point.Cadence = cadence; break;
            case "power" when TryDouble(value, out var power): point.Power = power; break;
            case "atemp" or "temp" when TryDouble(value, out var temperature): point.Temperature = temperature; break;
        }
    }

    private static void FinishSegment(List<TrackSegment> segments, ref List<TrackPoint>? points, ref string? name)
    {
        if (points is { Count: > 0 }) segments.Add(new TrackSegment { Name = name, Points = points });
        points = null;
        name = null;
    }

    private static bool TryDouble(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private sealed class PointBuilder(double latitude, double longitude)
    {
        public double? Elevation { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public double? Speed { get; set; }
        public int? HeartRate { get; set; }
        public int? Cadence { get; set; }
        public double? Power { get; set; }
        public double? Temperature { get; set; }

        public TrackPoint Build() => new()
        {
            Latitude = latitude,
            Longitude = longitude,
            ElevationMeters = Elevation,
            Timestamp = Timestamp,
            SpeedMetersPerSecond = Speed,
            HeartRateBpm = HeartRate,
            CadenceRpm = Cadence,
            PowerWatts = Power,
            TemperatureCelsius = Temperature
        };
    }
}

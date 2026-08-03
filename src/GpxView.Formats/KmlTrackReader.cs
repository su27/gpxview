using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using GpxView.Core;

namespace GpxView.Formats;

public sealed class KmlTrackReader(bool compressed = false) : ITrackReader
{
    public TrackFileFormat Format => compressed ? TrackFileFormat.Kmz : TrackFileFormat.Kml;

    public TrackDocument Read(Stream stream, string sourcePath)
    {
        if (!compressed) return ReadKml(stream, sourcePath);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.Entries
            .Where(item => item.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => string.Equals(item.Name, "doc.kml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.FullName.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException("KMZ 文件中没有找到 KML 文档。");
        using var kmlStream = entry.Open();
        return ReadKml(kmlStream, sourcePath) with { Format = TrackFileFormat.Kmz };
    }

    private TrackDocument ReadKml(Stream stream, string sourcePath)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var xmlReader = XmlReader.Create(stream, settings);
        var xml = XDocument.Load(xmlReader, LoadOptions.None);
        var segments = new List<TrackSegment>();
        var waypoints = new List<TrackWaypoint>();

        foreach (var placemark in xml.Descendants().Where(element => element.Name.LocalName == "Placemark"))
        {
            var name = placemark.Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value.Trim();
            var description = placemark.Elements().FirstOrDefault(element => element.Name.LocalName == "description")?.Value.Trim();

            foreach (var point in placemark.Descendants().Where(element => element.Name.LocalName == "Point"))
            {
                var waypoint = ParseWaypoint(point, name, description);
                if (waypoint is not null) waypoints.Add(waypoint);
            }

            foreach (var lineString in placemark.Descendants().Where(element => element.Name.LocalName == "LineString"))
            {
                var coordinates = lineString.Descendants().FirstOrDefault(element => element.Name.LocalName == "coordinates")?.Value;
                var points = ParseCoordinates(coordinates).ToArray();
                if (points.Length > 0) segments.Add(new TrackSegment { Name = name, Points = points });
            }

            foreach (var track in placemark.Descendants().Where(element => element.Name.LocalName == "Track"))
            {
                var times = track.Elements().Where(element => element.Name.LocalName == "when")
                    .Select(ParseTime).ToArray();
                var coordinates = track.Elements().Where(element => element.Name.LocalName == "coord")
                    .Select(ParseGxCoordinate).Where(point => point is not null).Cast<TrackPoint>().ToArray();
                var points = coordinates.Select((point, index) => point with
                {
                    Timestamp = index < times.Length ? times[index] : null
                }).ToArray();
                if (points.Length > 0) segments.Add(new TrackSegment { Name = name, Points = points });
            }
        }

        // Some generated KML files place LineString directly under Document.
        if (segments.Count == 0)
        {
            foreach (var lineString in xml.Descendants().Where(element => element.Name.LocalName == "LineString"))
            {
                var coordinates = lineString.Descendants().FirstOrDefault(element => element.Name.LocalName == "coordinates")?.Value;
                var points = ParseCoordinates(coordinates).ToArray();
                if (points.Length > 0) segments.Add(new TrackSegment { Points = points });
            }
        }

        var documentName = xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "Document")?
            .Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value.Trim();

        return new TrackDocument
        {
            Name = string.IsNullOrWhiteSpace(documentName) ? Path.GetFileNameWithoutExtension(sourcePath) : documentName,
            SourcePath = sourcePath,
            Format = TrackFileFormat.Kml,
            Segments = segments,
            Waypoints = waypoints
        };
    }

    private static IEnumerable<TrackPoint> ParseCoordinates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var tuple in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = tuple.Split(',');
            if (parts.Length < 2 || !TryDouble(parts[0], out var longitude) || !TryDouble(parts[1], out var latitude))
                continue;
            double? elevation = parts.Length > 2 && TryDouble(parts[2], out var parsedElevation) ? parsedElevation : null;
            yield return new TrackPoint { Latitude = latitude, Longitude = longitude, ElevationMeters = elevation };
        }
    }

    private static TrackWaypoint? ParseWaypoint(XElement point, string? name, string? description)
    {
        var coordinates = point.Descendants().FirstOrDefault(element => element.Name.LocalName == "coordinates")?.Value;
        var trackPoint = ParseCoordinates(coordinates).FirstOrDefault();
        if (trackPoint is null) return null;
        return new TrackWaypoint
        {
            Latitude = trackPoint.Latitude,
            Longitude = trackPoint.Longitude,
            ElevationMeters = trackPoint.ElevationMeters,
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description
        };
    }

    private static TrackPoint? ParseGxCoordinate(XElement element)
    {
        var parts = element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !TryDouble(parts[0], out var longitude) || !TryDouble(parts[1], out var latitude))
            return null;
        double? elevation = parts.Length > 2 && TryDouble(parts[2], out var parsedElevation) ? parsedElevation : null;
        return new TrackPoint { Latitude = latitude, Longitude = longitude, ElevationMeters = elevation };
    }

    private static DateTimeOffset? ParseTime(XElement element) =>
        DateTimeOffset.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value : null;

    private static bool TryDouble(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}

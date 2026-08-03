using System.IO;
using System.Text.Json;
using GpxView.Core;

namespace GpxView.App;

internal sealed record PreviewPoint(double X, double Y);

internal sealed record RecentTrackEntry
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required string Format { get; init; }
    public required double DistanceMeters { get; init; }
    public required double ElevationGainMeters { get; init; }
    public required bool HasElevation { get; init; }
    public string? PlaceName { get; init; }
    public required double RepresentativeLatitude { get; init; }
    public required double RepresentativeLongitude { get; init; }
    public required DateTimeOffset LastOpenedUtc { get; init; }
    public PreviewPoint[][] TrackPreview { get; init; } = [];
    public PreviewPoint[] ElevationPreview { get; init; } = [];
}

internal sealed record RecentTrackCache
{
    public int Version { get; init; } = 1;
    public List<RecentTrackEntry> Entries { get; init; } = [];
}

internal sealed class RecentTrackStore
{
    private const int MaximumEntries = 20;
    private readonly string cachePath;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly List<RecentTrackEntry> entries;

    public RecentTrackStore(string? cachePath = null)
    {
        this.cachePath = cachePath ?? AppPaths.RecentTracksFile;
        entries = Load().Take(MaximumEntries).ToList();
    }

    public IReadOnlyList<RecentTrackEntry> Entries => entries;

    public RecentTrackEntry? Find(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return entries.FirstOrDefault(entry => string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public void Upsert(RecentTrackEntry entry)
    {
        entries.RemoveAll(candidate => string.Equals(candidate.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, entry);
        if (entries.Count > MaximumEntries) entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
    }

    public bool Remove(string path) =>
        entries.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = cachePath + ".tmp";
            var json = JsonSerializer.Serialize(new RecentTrackCache { Entries = entries }, jsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Recent history is optional; file loading must continue even if the cache cannot be written.
        }
    }

    private IEnumerable<RecentTrackEntry> Load()
    {
        if (!File.Exists(cachePath)) return [];
        try
        {
            var cache = JsonSerializer.Deserialize<RecentTrackCache>(File.ReadAllText(cachePath), jsonOptions);
            return cache is { Version: 1 }
                ? cache.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                    .OrderByDescending(entry => entry.LastOpenedUtc)
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

internal static class RecentTrackEntryFactory
{
    private const int MaximumTrackPreviewPoints = 220;
    private const int MaximumElevationPreviewPoints = 180;

    public static RecentTrackEntry Create(
        string path,
        TrackDocument document,
        TrackStatistics statistics,
        RecentTrackEntry? previous)
    {
        var fullPath = Path.GetFullPath(path);
        var representativePoint = GetRepresentativePoint(document);
        var latitude = representativePoint?.Latitude ?? double.NaN;
        var longitude = representativePoint?.Longitude ?? double.NaN;
        var keepPreviousPlace = previous is not null
                                && !string.IsNullOrWhiteSpace(previous.PlaceName)
                                && CoordinatesAreNearby(previous.RepresentativeLatitude,
                                    previous.RepresentativeLongitude, latitude, longitude);

        return new RecentTrackEntry
        {
            Path = fullPath,
            FileName = Path.GetFileName(fullPath),
            Format = document.Format.ToString().ToUpperInvariant(),
            DistanceMeters = statistics.DistanceMeters,
            ElevationGainMeters = statistics.ElevationGainMeters,
            HasElevation = statistics.MinimumElevationMeters is not null,
            PlaceName = keepPreviousPlace ? previous!.PlaceName : null,
            RepresentativeLatitude = latitude,
            RepresentativeLongitude = longitude,
            LastOpenedUtc = DateTimeOffset.UtcNow,
            TrackPreview = BuildTrackPreview(document),
            ElevationPreview = BuildElevationPreview(document)
        };
    }

    private static TrackPoint? GetRepresentativePoint(TrackDocument document)
    {
        var segment = document.Segments.Where(candidate => candidate.Points.Count > 0)
            .MaxBy(candidate => candidate.Points.Count);
        if (segment is not null) return segment.Points[segment.Points.Count / 2];
        var waypoint = document.Waypoints.FirstOrDefault();
        return waypoint is null
            ? null
            : new TrackPoint { Latitude = waypoint.Latitude, Longitude = waypoint.Longitude };
    }

    private static bool CoordinatesAreNearby(double firstLatitude, double firstLongitude,
        double secondLatitude, double secondLongitude)
    {
        if (!double.IsFinite(firstLatitude) || !double.IsFinite(firstLongitude)
            || !double.IsFinite(secondLatitude) || !double.IsFinite(secondLongitude)) return false;

        var latitudeDelta = (firstLatitude - secondLatitude) * 111_000;
        var longitudeDelta = (firstLongitude - secondLongitude) * 111_000
                             * Math.Cos((firstLatitude + secondLatitude) * Math.PI / 360);
        return Math.Sqrt(latitudeDelta * latitudeDelta + longitudeDelta * longitudeDelta) <= 5_000;
    }

    private static PreviewPoint[][] BuildTrackPreview(TrackDocument document)
    {
        var totalPointCount = Math.Max(1, document.PointCount);
        var stride = Math.Max(1, (int)Math.Ceiling(totalPointCount / (double)MaximumTrackPreviewPoints));
        var sampledSegments = document.Segments
            .Select(segment => SampleSegment(segment.Points, stride))
            .Where(points => points.Count > 0)
            .ToArray();
        if (sampledSegments.Length == 0) return [];

        var allPoints = sampledSegments.SelectMany(points => points).ToArray();
        var meanLatitude = allPoints.Average(point => point.Latitude);
        var longitudeScale = Math.Max(.05, Math.Cos(meanLatitude * Math.PI / 180));
        var projected = allPoints.Select(point => (X: point.Longitude * longitudeScale, Y: point.Latitude)).ToArray();
        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        var range = Math.Max(maximumX - minimumX, maximumY - minimumY);
        if (range <= double.Epsilon) range = 1;
        var centerX = (minimumX + maximumX) / 2;
        var centerY = (minimumY + maximumY) / 2;

        return sampledSegments.Select(segment => segment.Select(point => new PreviewPoint(
                Math.Round(.5 + (point.Longitude * longitudeScale - centerX) / range * .86, 4),
                Math.Round(.5 - (point.Latitude - centerY) / range * .86, 4)))
            .ToArray()).ToArray();
    }

    private static List<TrackPoint> SampleSegment(IReadOnlyList<TrackPoint> points, int stride)
    {
        var result = new List<TrackPoint>();
        for (var index = 0; index < points.Count; index += stride) result.Add(points[index]);
        if (points.Count > 1 && (result.Count == 0 || !ReferenceEquals(result[^1], points[^1]))) result.Add(points[^1]);
        return result;
    }

    private static PreviewPoint[] BuildElevationPreview(TrackDocument document)
    {
        var points = document.Segments.SelectMany(segment => segment.Points).ToArray();
        if (points.Length == 0) return [];
        var stride = Math.Max(1, (int)Math.Ceiling(points.Length / (double)MaximumElevationPreviewPoints));
        var values = points.Select((point, index) => (Index: index, Elevation: point.ElevationMeters))
            .Where(value => value.Elevation is not null && (value.Index % stride == 0 || value.Index == points.Length - 1))
            .Select(value => (value.Index, Elevation: value.Elevation!.Value))
            .ToList();
        if (points[^1].ElevationMeters is { } finalElevation && values.All(value => value.Index != points.Length - 1))
            values.Add((points.Length - 1, finalElevation));
        if (values.Count == 0) return [];

        var minimum = values.Min(value => value.Elevation);
        var maximum = values.Max(value => value.Elevation);
        var range = Math.Max(1, maximum - minimum);
        return values.Select(value => new PreviewPoint(
                Math.Round(.05 + value.Index / (double)Math.Max(1, points.Length - 1) * .9, 4),
                Math.Round(.9 - (value.Elevation - minimum) / range * .8, 4)))
            .ToArray();
    }
}

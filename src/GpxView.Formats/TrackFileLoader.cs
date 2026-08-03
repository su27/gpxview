using System.Text;
using GpxView.Core;
using GpxView.Geo;

namespace GpxView.Formats;

public sealed class TrackFileLoader
{
    private readonly IReadOnlyDictionary<TrackFileFormat, ITrackReader> readers;

    public TrackFileLoader()
    {
        var availableReaders = new ITrackReader[]
        {
            new GpxTrackReader(),
            new KmlTrackReader(),
            new KmlTrackReader(compressed: true),
            new FitTrackReader()
        };
        readers = availableReaders.ToDictionary(reader => reader.Format);
    }

    public Task<TrackDocument> LoadAsync(
        string path,
        TrackLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new TrackLoadOptions();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var format = DetectFormat(path, stream);
            stream.Position = 0;
            var document = readers[format].Read(stream, path);
            if (document.PointCount == 0 && document.WaypointCount == 0)
                throw new InvalidDataException("文件中没有可显示的轨迹点或标注点。");
            return CoordinateConverter.ToWgs84(document, options.SourceCoordinateSystem);
        }, cancellationToken);
    }

    public static TrackFileFormat DetectFormat(string path, Stream stream)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var extensionFormat = extension switch
        {
            ".gpx" => TrackFileFormat.Gpx,
            ".kml" => TrackFileFormat.Kml,
            ".kmz" => TrackFileFormat.Kmz,
            ".fit" => TrackFileFormat.Fit,
            _ => (TrackFileFormat?)null
        };
        if (extensionFormat is not null) return extensionFormat.Value;

        if (!stream.CanSeek) throw new NotSupportedException("无法检测不可定位数据流的格式。");
        var originalPosition = stream.Position;
        Span<byte> header = stackalloc byte[512];
        var count = stream.Read(header);
        stream.Position = originalPosition;
        var bytes = header[..count];

        if (count >= 12 && bytes[8] == (byte)'.' && bytes[9] == (byte)'F'
            && bytes[10] == (byte)'I' && bytes[11] == (byte)'T')
            return TrackFileFormat.Fit;
        if (count >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
            return TrackFileFormat.Kmz;

        var text = Encoding.UTF8.GetString(bytes).ToLowerInvariant();
        if (text.Contains("<gpx")) return TrackFileFormat.Gpx;
        if (text.Contains("<kml") || text.Contains(":kml")) return TrackFileFormat.Kml;
        throw new NotSupportedException("仅支持 GPX、KML、KMZ 和 FIT 轨迹文件。");
    }
}

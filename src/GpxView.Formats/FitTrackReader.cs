using Dynastream.Fit;
using GpxView.Core;
using FitDateTime = Dynastream.Fit.DateTime;

namespace GpxView.Formats;

public sealed class FitTrackReader : ITrackReader
{
    private const double SemicirclesToDegrees = 180d / 2147483648d;

    public TrackFileFormat Format => TrackFileFormat.Fit;

    public TrackDocument Read(Stream stream, string sourcePath)
    {
        if (!stream.CanSeek) throw new InvalidDataException("FIT 解码需要可定位的数据流。");

        var decoder = new Decode();
        if (!decoder.IsFIT(stream)) throw new InvalidDataException("文件不是有效的 FIT 文件。");
        stream.Position = 0;

        var points = new List<TrackPoint>();
        var broadcaster = new MesgBroadcaster();
        decoder.MesgEvent += broadcaster.OnMesg;
        decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        broadcaster.RecordMesgEvent += (_, args) =>
        {
            var record = new RecordMesg(args.mesg);
            var latitude = record.GetPositionLat();
            var longitude = record.GetPositionLong();
            if (latitude is null || longitude is null) return;

            var altitude = record.GetEnhancedAltitude() ?? record.GetAltitude();
            var speed = record.GetEnhancedSpeed() ?? record.GetSpeed();
            points.Add(new TrackPoint
            {
                Latitude = latitude.Value * SemicirclesToDegrees,
                Longitude = longitude.Value * SemicirclesToDegrees,
                ElevationMeters = altitude,
                Timestamp = ToDateTimeOffset(record.GetTimestamp()),
                SpeedMetersPerSecond = speed,
                HeartRateBpm = record.GetHeartRate(),
                CadenceRpm = record.GetCadence(),
                PowerWatts = record.GetPower(),
                TemperatureCelsius = record.GetTemperature()
            });
        };

        try
        {
            if (!decoder.Read(stream)) throw new InvalidDataException("FIT 文件解码未完成，文件可能损坏。");
        }
        catch (FitException exception)
        {
            throw new InvalidDataException("FIT 文件损坏或包含不受支持的数据。", exception);
        }

        return new TrackDocument
        {
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            Format = Format,
            Segments = points.Count == 0 ? [] : [new TrackSegment { Points = points }]
        };
    }

    private static DateTimeOffset? ToDateTimeOffset(FitDateTime? value)
    {
        if (value is null) return null;
        var dateTime = System.DateTime.SpecifyKind(value.GetDateTime(), DateTimeKind.Utc);
        return new DateTimeOffset(dateTime);
    }
}

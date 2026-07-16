using System.IO.Compression;
using System.Text;
using Dynastream.Fit;
using GpxView.Core;
using GpxView.Formats;
using GpxView.Geo;
using FitDateTime = Dynastream.Fit.DateTime;
using FitFile = Dynastream.Fit.File;

namespace GpxView.Tests;

public class TrackReaderTests
{
    [Fact]
    public void GpxReader_ParsesTrackAndSensorExtensions()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" creator="tests" xmlns="http://www.topografix.com/GPX/1/1"
                 xmlns:gpxtpx="http://www.garmin.com/xmlschemas/TrackPointExtension/v1">
              <metadata><name>晨跑</name></metadata>
              <trk><trkseg>
                <trkpt lat="39.9" lon="116.3"><ele>42.5</ele><time>2026-07-16T00:00:00Z</time>
                  <extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>128</gpxtpx:hr><gpxtpx:cad>86</gpxtpx:cad></gpxtpx:TrackPointExtension></extensions>
                </trkpt>
                <trkpt lat="39.901" lon="116.301"><ele>45.0</ele><time>2026-07-16T00:00:10Z</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;

        using var stream = Utf8Stream(xml);
        var document = new GpxTrackReader().Read(stream, "morning.gpx");

        Assert.Equal("晨跑", document.Name);
        Assert.Equal(2, document.PointCount);
        var first = document.Segments[0].Points[0];
        Assert.Equal(42.5, first.ElevationMeters);
        Assert.Equal(128, first.HeartRateBpm);
        Assert.Equal(86, first.CadenceRpm);
        Assert.NotNull(first.Timestamp);
    }

    [Fact]
    public void KmlReader_ParsesLineString()
    {
        const string xml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>路线</name><Placemark>
              <name>第一段</name><LineString><coordinates>116.3,39.9,10 116.31,39.91,20</coordinates></LineString>
            </Placemark></Document></kml>
            """;
        using var stream = Utf8Stream(xml);

        var document = new KmlTrackReader().Read(stream, "route.kml");

        Assert.Equal("路线", document.Name);
        Assert.Equal(2, document.PointCount);
        Assert.Equal("第一段", document.Segments[0].Name);
        Assert.Equal(20d, document.Segments[0].Points[1].ElevationMeters);
    }

    [Fact]
    public void KmzReader_ParsesEmbeddedDocument()
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("doc.kml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("<kml><Document><Placemark><LineString><coordinates>1,2,3 4,5,6</coordinates></LineString></Placemark></Document></kml>");
        }
        archiveStream.Position = 0;

        var document = new KmlTrackReader(compressed: true).Read(archiveStream, "route.kmz");

        Assert.Equal(TrackFileFormat.Kmz, document.Format);
        Assert.Equal(2, document.PointCount);
    }

    [Fact]
    public void FitReader_DecodesGpsAndActivityValues()
    {
        using var encoded = new MemoryStream();
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(encoded);
        var timestamp = new FitDateTime(new System.DateTime(2026, 7, 16, 8, 0, 0, DateTimeKind.Utc));
        var fileId = new FileIdMesg();
        fileId.SetType(FitFile.Activity);
        fileId.SetManufacturer(1);
        fileId.SetProduct(1);
        fileId.SetTimeCreated(timestamp);
        encoder.Write(fileId);
        var record = new RecordMesg();
        record.SetTimestamp(timestamp);
        record.SetPositionLat(ToSemicircles(39.9));
        record.SetPositionLong(ToSemicircles(116.3));
        record.SetAltitude(50);
        record.SetSpeed(3.5f);
        record.SetHeartRate(135);
        record.SetCadence(88);
        record.SetPower(210);
        encoder.Write(record);
        encoder.Close();

        using var input = new MemoryStream(encoded.ToArray());
        var document = new FitTrackReader().Read(input, "activity.fit");

        Assert.Single(document.Segments);
        var point = Assert.Single(document.Segments[0].Points);
        Assert.InRange(point.Latitude, 39.8999, 39.9001);
        Assert.InRange(point.Longitude, 116.2999, 116.3001);
        Assert.Equal(135, point.HeartRateBpm);
        Assert.Equal(210d, point.PowerWatts);
    }

    private static int ToSemicircles(double degrees) => (int)Math.Round(degrees / 180d * 2147483648d);
    private static MemoryStream Utf8Stream(string value) => new(Encoding.UTF8.GetBytes(value));
}

public class TrackStatisticsTests
{
    [Fact]
    public void Calculate_ComputesDistanceDurationAndActivitySummary()
    {
        var start = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var document = new TrackDocument
        {
            Name = "test",
            SourcePath = "test.gpx",
            Format = TrackFileFormat.Gpx,
            Segments = [new TrackSegment
            {
                Points =
                [
                    new TrackPoint { Latitude = 0, Longitude = 0, ElevationMeters = 10, Timestamp = start, SpeedMetersPerSecond = 3, HeartRateBpm = 120 },
                    new TrackPoint { Latitude = 0, Longitude = 0.001, ElevationMeters = 15, Timestamp = start.AddSeconds(10), SpeedMetersPerSecond = 5, HeartRateBpm = 140 }
                ]
            }]
        };

        var statistics = TrackStatisticsCalculator.Calculate(document);

        Assert.InRange(statistics.DistanceMeters, 111, 112);
        Assert.Equal(TimeSpan.FromSeconds(10), statistics.Duration);
        Assert.Equal(5, statistics.ElevationGainMeters);
        Assert.Equal(4, statistics.AverageSpeedMetersPerSecond);
        Assert.Equal(130, statistics.AverageHeartRateBpm);
    }
}

public class CoordinateConverterTests
{
    [Fact]
    public void Gcj02ToWgs84_CorrectsKnownBeijingCoordinate()
    {
        var (latitude, longitude) = CoordinateConverter.Gcj02ToWgs84(39.908823, 116.397470);

        Assert.InRange(latitude, 39.906, 39.909);
        Assert.InRange(longitude, 116.389, 116.393);
    }

    [Fact]
    public void Gcj02ToWgs84_DoesNotChangeCoordinatesOutsideChina()
    {
        var result = CoordinateConverter.Gcj02ToWgs84(51.5074, -0.1278);
        Assert.Equal((51.5074, -0.1278), result);
    }
}


using System.Buffers.Binary;
using GpxView.Core;

namespace GpxView.Tests;

public sealed class PmTilesArchiveTests
{
    [Fact]
    public void TryOpenReadsRasterHeaderAndByteRanges()
    {
        var path = CreateArchive();
        try
        {
            var archive = PmTilesArchive.TryOpen(path);

            Assert.NotNull(archive);
            Assert.Equal(11, archive.MinZoom);
            Assert.Equal(16, archive.MaxZoom);
            Assert.Equal(115.9, archive.West, 6);
            Assert.Equal(39.9, archive.South, 6);
            Assert.Equal(116.0, archive.East, 6);
            Assert.Equal(40.0, archive.North, 6);

            Assert.True(archive.TryReadRange("bytes=125-132", out var content, out var start, out var end));
            Assert.Equal(125, start);
            Assert.Equal(132, end);
            Assert.Equal(File.ReadAllBytes(path)[125..133], content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("items=0-10")]
    [InlineData("bytes=-10")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=999-1000")]
    [InlineData("bytes=10-5")]
    public void TryReadRangeRejectsUnsupportedOrInvalidRanges(string range)
    {
        var path = CreateArchive();
        try
        {
            var archive = Assert.IsType<PmTilesArchive>(PmTilesArchive.TryOpen(path));
            Assert.False(archive.TryReadRange(range, out _, out _, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryOpenRejectsNonPmTilesFiles()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[127]);
            Assert.Null(PmTilesArchive.TryOpen(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void TryOpenAcceptsTransparentRasterTileTypes(byte tileType)
    {
        var path = CreateArchive(tileType);
        try
        {
            Assert.NotNull(PmTilesArchive.TryOpen(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void TryOpenRejectsOtherTileTypes(byte tileType)
    {
        var path = CreateArchive(tileType);
        try
        {
            Assert.Null(PmTilesArchive.TryOpen(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DiscoverReturnsValidArchivesInStableFileNameOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gpxview-pmtiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            CreateArchive(Path.Combine(folder, "z-last.pmtiles"), 4);
            File.WriteAllBytes(Path.Combine(folder, "invalid.pmtiles"), new byte[127]);
            CreateArchive(Path.Combine(folder, "a-first.pmtiles"), 2);
            CreateArchive(Path.Combine(folder, "ignored.bin"), 2);

            var archives = PmTilesArchive.Discover(folder);

            Assert.Equal(
                ["a-first.pmtiles", "z-last.pmtiles"],
                archives.Select(archive => Path.GetFileName(archive.Path)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DiscoverReturnsEmptyForMissingFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gpxview-missing-{Guid.NewGuid():N}");

        Assert.Empty(PmTilesArchive.Discover(folder));
    }

    private static string CreateArchive(byte tileType = 2)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gpxview-{Guid.NewGuid():N}.pmtiles");
        return CreateArchive(path, tileType);
    }

    private static string CreateArchive(string path, byte tileType)
    {
        var data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        "PMTiles"u8.CopyTo(data);
        data[7] = 3;
        data[99] = tileType;
        data[100] = 11;
        data[101] = 16;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(102, 4), 1_159_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(106, 4), 399_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(110, 4), 1_160_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(114, 4), 400_000_000);
        File.WriteAllBytes(path, data);
        return path;
    }
}

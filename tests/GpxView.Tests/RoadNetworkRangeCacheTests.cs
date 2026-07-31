using GpxView.Core;

namespace GpxView.Tests;

public sealed class RoadNetworkRangeCacheTests
{
    [Fact]
    public void TryWriteAndTryReadRoundTripsExactRanges()
    {
        var folder = CreateTemporaryFolder();
        try
        {
            var cache = new RoadNetworkRangeCache(folder);
            var content = new byte[] { 1, 2, 3, 4 };

            Assert.True(cache.TryWrite("endpoint\nbeijing-density", "\"v1\"", 10, 13, content));
            Assert.True(cache.TryRead("endpoint\nbeijing-density", "\"v1\"", 10, 13, out var cached));

            Assert.Equal(content, cached);
            var stats = cache.GetStats();
            Assert.Equal(4, stats.Bytes);
            Assert.Equal(1, stats.Entries);
        }
        finally
        {
            DeleteTemporaryFolder(folder);
        }
    }

    [Fact]
    public void DifferentEtagDoesNotReuseCachedContent()
    {
        var folder = CreateTemporaryFolder();
        try
        {
            var cache = new RoadNetworkRangeCache(folder);
            Assert.True(cache.TryWrite("endpoint\nbeijing-density", "\"old\"", 0, 2, [7, 8, 9]));

            Assert.False(cache.TryRead("endpoint\nbeijing-density", "\"new\"", 0, 2, out _));
        }
        finally
        {
            DeleteTemporaryFolder(folder);
        }
    }

    [Fact]
    public void TryWriteRejectsContentLengthMismatch()
    {
        var folder = CreateTemporaryFolder();
        try
        {
            var cache = new RoadNetworkRangeCache(folder);

            Assert.False(cache.TryWrite("endpoint\nbeijing-density", "\"v1\"", 0, 3, [1, 2]));
            Assert.Equal(0, cache.GetStats().Entries);
        }
        finally
        {
            DeleteTemporaryFolder(folder);
        }
    }

    [Fact]
    public void ClearRemovesCachedEntries()
    {
        var folder = CreateTemporaryFolder();
        try
        {
            var cache = new RoadNetworkRangeCache(folder);
            Assert.True(cache.TryWrite("endpoint\nbeijing-density", "\"v1\"", 0, 2, [1, 2, 3]));

            cache.Clear();

            Assert.Equal(new RoadNetworkRangeCacheStats(0, 0), cache.GetStats());
            Assert.False(cache.TryRead("endpoint\nbeijing-density", "\"v1\"", 0, 2, out _));
        }
        finally
        {
            DeleteTemporaryFolder(folder);
        }
    }

    private static string CreateTemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gpxview-road-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteTemporaryFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

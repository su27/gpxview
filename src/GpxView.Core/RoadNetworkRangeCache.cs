using System.Security.Cryptography;
using System.Text;

namespace GpxView.Core;

public sealed record RoadNetworkRangeCacheStats(long Bytes, int Entries);

public sealed class RoadNetworkRangeCache(string rootFolder)
{
    private const int MaximumEntryLength = 16 * 1024 * 1024;
    private readonly object gate = new();

    public RoadNetworkRangeCacheStats GetStats()
    {
        lock (gate)
        {
            if (!Directory.Exists(rootFolder)) return new RoadNetworkRangeCacheStats(0, 0);
            try
            {
                long bytes = 0;
                var entries = 0;
                foreach (var path in Directory.EnumerateFiles(rootFolder, "*.bin", SearchOption.AllDirectories))
                {
                    var file = new FileInfo(path);
                    bytes += file.Length;
                    entries++;
                }
                return new RoadNetworkRangeCacheStats(bytes, entries);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new RoadNetworkRangeCacheStats(0, 0);
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (Directory.Exists(rootFolder)) Directory.Delete(rootFolder, recursive: true);
            Directory.CreateDirectory(rootFolder);
        }
    }

    public bool TryRead(
        string archiveKey,
        string etag,
        long start,
        long end,
        out byte[] content)
    {
        content = [];
        var expectedLength = end - start + 1;
        if (!IsSupportedRange(expectedLength)) return false;

        lock (gate)
        {
            var path = GetEntryPath(archiveKey, etag, start, end);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return false;
                if (file.Length != expectedLength)
                {
                    file.Delete();
                    return false;
                }
                content = File.ReadAllBytes(path);
                return content.LongLength == expectedLength;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                content = [];
                return false;
            }
        }
    }

    public bool TryWrite(
        string archiveKey,
        string etag,
        long start,
        long end,
        ReadOnlySpan<byte> content)
    {
        var expectedLength = end - start + 1;
        if (!IsSupportedRange(expectedLength) || content.Length != expectedLength) return false;

        lock (gate)
        {
            var path = GetEntryPath(archiveKey, etag, start, end);
            string? temporaryPath = null;
            try
            {
                var folder = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(folder);
                if (File.Exists(path)) return true;

                temporaryPath = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temporaryPath, content);
                File.Move(temporaryPath, path, overwrite: false);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDeleteTemporaryFile(temporaryPath);
                return false;
            }
        }
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed cache write should not affect map rendering.
        }
    }

    private string GetEntryPath(string archiveKey, string etag, long start, long end) =>
        Path.Combine(rootFolder, Hash(archiveKey), Hash(etag), $"{start:x16}-{end:x16}.bin");

    private static bool IsSupportedRange(long length) =>
        length is > 0 and <= MaximumEntryLength and <= int.MaxValue;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

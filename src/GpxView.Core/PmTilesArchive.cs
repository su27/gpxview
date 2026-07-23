using System.Buffers.Binary;

namespace GpxView.Core;

public sealed class PmTilesArchive
{
    private const int HeaderLength = 127;
    private const int MaximumRangeLength = 16 * 1024 * 1024;
    private const byte PngTileType = 2;
    private const byte WebpTileType = 4;

    private PmTilesArchive(
        string path,
        long length,
        DateTime lastWriteTimeUtc,
        byte minZoom,
        byte maxZoom,
        double west,
        double south,
        double east,
        double north)
    {
        Path = path;
        Length = length;
        ETag = $"\"{length:x}-{lastWriteTimeUtc.Ticks:x}\"";
        MinZoom = minZoom;
        MaxZoom = maxZoom;
        West = west;
        South = south;
        East = east;
        North = north;
    }

    public string Path { get; }
    public long Length { get; }
    public string ETag { get; }
    public byte MinZoom { get; }
    public byte MaxZoom { get; }
    public double West { get; }
    public double South { get; }
    public double East { get; }
    public double North { get; }

    public static IReadOnlyList<PmTilesArchive> Discover(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        try
        {
            return Directory
                .EnumerateFiles(folder, "*.pmtiles", SearchOption.TopDirectoryOnly)
                .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(TryOpen)
                .OfType<PmTilesArchive>()
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static PmTilesArchive? TryOpen(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < HeaderLength) return null;
            Span<byte> header = stackalloc byte[HeaderLength];
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadExactly(header);
            var tileType = header[99];
            if (!header[..7].SequenceEqual("PMTiles"u8)
                || header[7] != 3
                || tileType is not (PngTileType or WebpTileType))
            {
                return null;
            }

            return new PmTilesArchive(
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc,
                header[100],
                header[101],
                BinaryPrimitives.ReadInt32LittleEndian(header[102..106]) / 10_000_000d,
                BinaryPrimitives.ReadInt32LittleEndian(header[106..110]) / 10_000_000d,
                BinaryPrimitives.ReadInt32LittleEndian(header[110..114]) / 10_000_000d,
                BinaryPrimitives.ReadInt32LittleEndian(header[114..118]) / 10_000_000d);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool TryReadRange(
        string? rangeHeader,
        out byte[] content,
        out long start,
        out long end)
    {
        content = [];
        start = 0;
        end = Length - 1;
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            const string prefix = "bytes=";
            if (!rangeHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var range = rangeHeader[prefix.Length..];
            if (range.Contains(',')) return false;
            var separator = range.IndexOf('-');
            if (separator <= 0 || !long.TryParse(range[..separator], out start)) return false;
            if (separator + 1 < range.Length && !long.TryParse(range[(separator + 1)..], out end)) return false;
        }

        if (start < 0 || start >= Length) return false;
        end = Math.Min(end, Length - 1);
        var rangeLength = end - start + 1;
        if (rangeLength <= 0 || rangeLength > MaximumRangeLength || rangeLength > int.MaxValue) return false;

        content = GC.AllocateUninitializedArray<byte>((int)rangeLength);
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = start;
        stream.ReadExactly(content);
        return true;
    }
}

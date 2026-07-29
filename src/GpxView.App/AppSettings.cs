using System.IO;
using System.Text.Json;

namespace GpxView.App;

internal sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "system";
    public bool? GeocodingEnabled { get; init; }
    public string RoadNetworkServiceEndpoint { get; init; } = string.Empty;
    public string RoadNetworkAccountId { get; init; } = string.Empty;
    public string RoadNetworkDisplayName { get; init; } = string.Empty;
    public string RoadNetworkDeviceId { get; init; } = string.Empty;
}

internal sealed class AppSettingsStore
{
    private readonly string path;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppSettingsStore(string? path = null) => this.path = path ?? AppPaths.SettingsFile;

    public AppSettings Load()
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), jsonOptions);
            return settings is { Version: 1 } ? Normalize(settings) : new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(settings), jsonOptions));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Settings are optional; the app remains usable with defaults if persistence fails.
        }
    }

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        Language = settings.Language is "zh-CN" or "en-US" ? settings.Language : "system",
        RoadNetworkServiceEndpoint = settings.RoadNetworkServiceEndpoint.Trim(),
        RoadNetworkAccountId = settings.RoadNetworkAccountId.Trim(),
        RoadNetworkDisplayName = settings.RoadNetworkDisplayName.Trim(),
        RoadNetworkDeviceId = settings.RoadNetworkDeviceId.Trim()
    };
}

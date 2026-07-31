using System.IO;

namespace GpxView.App;

internal static class AppPaths
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GpxView");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string RecentTracksFile => Path.Combine(DataFolder, "recent-tracks.json");
    public static string RoadNetworkFolder => Path.Combine(DataFolder, "RoadNetwork");
    public static string RoadNetworkCacheFolder => Path.Combine(DataFolder, "RoadNetworkCache");
    public static string WebViewDataFolder => Path.Combine(DataFolder, "WebView2");
}

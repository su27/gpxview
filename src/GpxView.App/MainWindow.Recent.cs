using System.IO;
using System.Text.Json;
using System.Windows;
using GpxView.Core;
using GpxView.Geo;

namespace GpxView.App;

public partial class MainWindow
{
    private const string DefaultGeocodingEndpoint = "https://nominatim.openstreetmap.org/reverse";
    private readonly RecentTrackStore recentTrackStore = new();
    private ReverseGeocoder? reverseGeocoder;
    private string? currentPlaceName;

    private void HandleWebReady()
    {
        webReady = true;
        SendTheme();
        SendMapStyle();
        if (currentDocument is not null)
        {
            SendToMap(currentDocument, currentStatistics ?? TrackStatisticsCalculator.Calculate(currentDocument));
            SendCurrentPlaceName();
        }
        SendRecentTracks();
        SetRecentPanelVisible(currentDocument is null);
    }

    private void HandleWebCommand(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String) return;

        switch (typeElement.GetString())
        {
            case "openRecentTrack" when message.TryGetProperty("path", out var pathElement)
                                             && pathElement.ValueKind == JsonValueKind.String:
                var path = pathElement.GetString();
                if (!string.IsNullOrWhiteSpace(path)) _ = OpenRecentTrackAsync(path);
                break;
            case "openFile":
                OnOpenFile(this, new RoutedEventArgs());
                break;
        }
    }

    private void OnRecentFiles(object sender, RoutedEventArgs e)
    {
        SendRecentTracks();
        SetRecentPanelVisible(true);
    }

    private async Task OpenRecentTrackAsync(string path)
    {
        var entry = recentTrackStore.Find(path);
        if (entry is null) return;
        if (!File.Exists(entry.Path))
        {
            recentTrackStore.Remove(entry.Path);
            recentTrackStore.Save();
            SendRecentTracks();
            SetRecentPanelVisible(true);
            MessageBox.Show(this, $"文件已不存在，已从最近记录中移除：\n{entry.Path}",
                "找不到文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetRecentPanelVisible(false);
        await LoadFileAsync(entry.Path);
    }

    private RecentTrackEntry RegisterRecentTrack(string path, TrackDocument document, TrackStatistics statistics)
    {
        var entry = RecentTrackEntryFactory.Create(path, document, statistics, recentTrackStore.Find(path));
        recentTrackStore.Upsert(entry);
        recentTrackStore.Save();
        currentPlaceName = entry.PlaceName;
        SendRecentTracks();
        SetRecentPanelVisible(false);
        return entry;
    }

    private async Task ResolvePlaceNameAsync(RecentTrackEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (entry.PlaceName is not null) return;
            var options = mapServices.Geocoding ?? new GeocodingOptions();
            if (!options.Enabled)
            {
                if (string.Equals(currentPath, entry.Path, StringComparison.OrdinalIgnoreCase)) SendCurrentPlaceName();
                return;
            }

            reverseGeocoder ??= new ReverseGeocoder(true,
                string.IsNullOrWhiteSpace(options.Endpoint) ? DefaultGeocodingEndpoint : options.Endpoint);
            var placeName = await reverseGeocoder.ResolvePlaceNameAsync(entry.RepresentativeLatitude,
                entry.RepresentativeLongitude, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(placeName))
            {
                if (string.Equals(currentPath, entry.Path, StringComparison.OrdinalIgnoreCase)) SendCurrentPlaceName(true);
                return;
            }

            var cachedEntry = recentTrackStore.Find(entry.Path);
            if (cachedEntry is null) return;
            recentTrackStore.Upsert(cachedEntry with { PlaceName = placeName });
            recentTrackStore.Save();
            SendRecentTracks();
            if (!string.Equals(currentPath, entry.Path, StringComparison.OrdinalIgnoreCase)) return;
            currentPlaceName = placeName;
            StatusText.Text = $"{Path.GetFileName(entry.Path)} · {placeName}";
            SendCurrentPlaceName();
        }
        catch (OperationCanceledException)
        {
            // Opening another file or closing the window cancels background place lookup.
        }
        catch (ObjectDisposedException)
        {
            // The HTTP client may be disposed while the window is closing.
        }
    }

    private void SendRecentTracks()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setRecentTracks", Entries = recentTrackStore.Entries };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SetRecentPanelVisible(bool visible)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setRecentPanelVisible", Visible = visible };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendCurrentPlaceName(bool lookupFailed = false)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var options = mapServices.Geocoding ?? new GeocodingOptions();
        var placeName = currentPlaceName
                        ?? (!options.Enabled ? "地点识别已关闭" : lookupFailed ? "地点暂不可用" : "正在识别地点…");
        var message = new { Type = "setPlaceName", PlaceName = placeName };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void CloseRecentTrackServices() => reverseGeocoder?.Dispose();
}

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
    private CancellationTokenSource geocodingCancellation = new();
    private ReverseGeocoder? reverseGeocoder;

    private const bool IsGeocodingAvailable = true;
    private bool IsGeocodingEnabled => IsGeocodingAvailable && appSettings.GeocodingEnabled == true;

    private void HandleWebReady()
    {
        webReady = true;
        SendLocalization();
        SendTheme();
        SendMapStyle();
        SendTerrainMode();
        SendRoadNetworkMode();
        SendRoadNetworkConfig();
        SendTrackCollection(fit: openTracks.Count > 0);
        SendCurrentPlaceName();
        SendRecentTracks();
        SendSettingsState();
        SetRecentPanelVisible(false);
        SetSettingsPanelVisible(false);
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
            case "closeSettings":
                SetSettingsPanelVisible(false);
                break;
            case "setLanguage" when TryReadString(message, "language", out var language):
                SetLanguage(language);
                break;
            case "setGeocodingEnabled" when message.TryGetProperty("enabled", out var geocodingElement)
                                                    && geocodingElement.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SetGeocodingEnabled(geocodingElement.GetBoolean());
                break;
            case "openDefaultAppsSettings":
                OpenDefaultAppsSettings();
                break;
            case "associateFileType" when TryReadString(message, "extension", out var extension):
                AssociateFileType(extension);
                break;
            case "openRoadNetworkFolder":
                OpenRoadNetworkFolder();
                break;
            case "refreshRoadNetworks":
                RefreshRoadNetworkArchives();
                SendSettingsState();
                break;
            case "clearRoadNetworkCache":
                _ = ClearRoadNetworkCacheAsync();
                break;
            case "connectRoadNetworkService"
                when TryReadString(message, "endpoint", out var endpoint)
                     && TryReadString(message, "enrollmentCode", out var enrollmentCode):
                _ = ConnectRoadNetworkServiceAsync(endpoint, enrollmentCode);
                break;
            case "disconnectRoadNetworkService":
                _ = DisconnectRoadNetworkServiceAsync();
                break;
            case "refreshOnlineRoadNetworks":
                _ = RefreshRemoteRoadNetworkAsync();
                break;
            case "openProjectHome":
                OpenProjectHome();
                break;
            case "selectTrack" when TryReadTrackId(message, out var selectedTrackId):
                SelectTrack(selectedTrackId);
                break;
            case "setTrackVisibility" when TryReadTrackId(message, out var visibleTrackId)
                                                   && message.TryGetProperty("visible", out var visibleElement)
                                                   && visibleElement.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SetTrackVisibility(visibleTrackId, visibleElement.GetBoolean());
                break;
            case "closeTrack" when TryReadTrackId(message, out var closedTrackId):
                CloseTrack(closedTrackId);
                break;
            case "terrainState" when message.TryGetProperty("enabled", out var enabledElement)
                                     && enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False:
                var error = message.TryGetProperty("error", out var errorElement)
                            && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
                SetTerrainState(enabledElement.GetBoolean(), error);
                break;
            case "mapError" when message.TryGetProperty("error", out var mapErrorElement)
                                 && mapErrorElement.ValueKind == JsonValueKind.String:
                StatusText.Text = mapErrorElement.GetString() ?? T("Status.MapUnavailable");
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
            MessageBox.Show(this, TF("Dialog.MissingFileMessage", entry.Path),
                T("Dialog.MissingFileTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetRecentPanelVisible(false);
        await LoadFilesAsync([entry.Path]);
    }

    private RecentTrackEntry RegisterRecentTrack(string path, TrackDocument document, TrackStatistics statistics)
    {
        var entry = RecentTrackEntryFactory.Create(path, document, statistics, recentTrackStore.Find(path));
        recentTrackStore.Upsert(entry);
        recentTrackStore.Save();
        SendRecentTracks();
        SetRecentPanelVisible(false);
        return entry;
    }

    private async Task ResolvePlaceNameAsync(RecentTrackEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (entry.PlaceName is not null) return;
            if (!IsGeocodingEnabled)
            {
                if (string.Equals(currentPath, entry.Path, StringComparison.OrdinalIgnoreCase)) SendCurrentPlaceName();
                return;
            }

            reverseGeocoder ??= new ReverseGeocoder(true, DefaultGeocodingEndpoint, localization.Locale);
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
            var openTrack = FindOpenTrackByPath(entry.Path);
            if (openTrack is not null) openTrack.PlaceName = placeName;
            if (!string.Equals(currentPath, entry.Path, StringComparison.OrdinalIgnoreCase)) return;
            UpdateCurrentTrackPresentation();
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
        var placeName = CurrentTrack?.PlaceName
                        ?? (!IsGeocodingEnabled
                            ? T("Place.Disabled")
                            : lookupFailed ? T("Place.Unavailable") : T("Place.Recognizing"));
        var message = new { Type = "setPlaceName", PlaceName = placeName };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private static bool TryReadTrackId(JsonElement message, out string id)
    {
        id = string.Empty;
        if (!message.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            return false;
        id = idElement.GetString() ?? string.Empty;
        return id.Length > 0;
    }

    private void StartPlaceNameResolution(RecentTrackEntry entry)
    {
        if (IsGeocodingEnabled && entry.PlaceName is null)
            _ = ResolvePlaceNameAsync(entry, geocodingCancellation.Token);
    }

    private void SetGeocodingEnabled(bool enabled)
    {
        enabled &= IsGeocodingAvailable;
        appSettings = appSettings with { GeocodingEnabled = enabled };
        appSettingsStore.Save(appSettings);
        ResetReverseGeocoder();
        SendSettingsState();
        SendCurrentPlaceName();
    }

    private void ResetReverseGeocoder()
    {
        geocodingCancellation.Cancel();
        geocodingCancellation.Dispose();
        geocodingCancellation = new CancellationTokenSource();
        reverseGeocoder?.Dispose();
        reverseGeocoder = null;
        if (!IsGeocodingEnabled) return;
        foreach (var track in openTracks)
        {
            var entry = recentTrackStore.Find(track.Path);
            if (entry is not null) StartPlaceNameResolution(entry);
        }
    }

    private void CloseRecentTrackServices()
    {
        geocodingCancellation.Cancel();
        geocodingCancellation.Dispose();
        reverseGeocoder?.Dispose();
    }
}

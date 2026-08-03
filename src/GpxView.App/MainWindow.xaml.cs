using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GpxView.Core;
using GpxView.Formats;
using GpxView.Geo;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace GpxView.App;

public partial class MainWindow : Window
{
    private const int WebProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gpx", ".kml", ".kmz", ".fit"
    };
    private static readonly string[] TrackColors =
    [
        "#176BDE", "#D85832", "#168C72", "#A85DB8", "#D18B12", "#5266C7",
        "#C8426F", "#63862A", "#1F8FA8", "#A86832", "#7656C2", "#C64D45"
    ];
    private readonly TrackFileLoader loader = new();
    private readonly MapServiceOptions mapServices = LoadMapServiceOptions();
    private readonly List<OpenTrackState> openTracks = [];
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CancellationTokenSource? loadCancellation;
    private string? activeTrackId;
    private int nextTrackOrdinal;
    private bool webReady;
    private DisplayTheme selectedTheme = DisplayTheme.System;
    private bool effectiveDarkTheme;
    private bool terrainEnabled;

    public MainWindow()
    {
        InitializeComponent();
        InitializeApplicationSettings();
        PopulateToolbarSelectors();
        InitializeRoadNetwork();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        ApplyLocalization();
        ApplyTheme();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewDataFolder);
            await MapView.EnsureCoreWebView2Async(webEnvironment);
            var core = MapView.CoreWebView2;
            MapView.AllowExternalDrop = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var mapServicesJson = JsonSerializer.Serialize(mapServices, JsonOptions);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.gpxViewMapServices={mapServicesJson};");
            var roadNetworkJson = JsonSerializer.Serialize(GetRoadNetworkWebConfig(), JsonOptions);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.gpxViewRoadNetwork={roadNetworkJson};");
            core.SetVirtualHostNameToFolderMapping(
                "app.gpxview",
                Path.Combine(AppContext.BaseDirectory, "Web"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            ConfigureRoadNetworkRequests(core);
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith("https://app.gpxview/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            MapView.Source = new Uri("https://app.gpxview/index.html");
            _ = RefreshRemoteRoadNetworkAsync();

            var startupPaths = Environment.GetCommandLineArgs().Skip(1)
                .Where(IsSupportedTrackFile)
                .Select(Path.GetFullPath)
                .ToArray();
            if (startupPaths.Length > 0) await LoadFilesAsync(startupPaths);
        }
        catch (Exception exception)
        {
            StatusText.Text = T("Status.WebViewFailed");
            MessageBox.Show(this, TF("Dialog.WebViewMessage", exception.Message),
                T("Dialog.WebViewTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (IsWebReadyMessage(message.RootElement))
            {
                HandleWebReady();
                return;
            }
            HandleWebCommand(message.RootElement);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            // Ignore malformed or unsupported messages from the local map page.
        }
    }

    private static bool IsWebReadyMessage(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.String)
            return message.GetString() == "ready";
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "ready") return false;
        return !message.TryGetProperty("protocolVersion", out var version)
               || version.ValueKind == JsonValueKind.Number
               && version.TryGetInt32(out var value)
               && value == WebProtocolVersion;
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        selectedTheme = selectedTheme switch
        {
            DisplayTheme.System => DisplayTheme.Light,
            DisplayTheme.Light => DisplayTheme.Dark,
            _ => DisplayTheme.System
        };
        ApplyTheme();
    }

    private void OnTerrainToggle(object sender, RoutedEventArgs e)
    {
        terrainEnabled = !terrainEnabled;
        UpdateTerrainButton();
        SendTerrainMode();
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (selectedTheme != DisplayTheme.System) return;
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    private void ApplyTheme()
    {
        effectiveDarkTheme = selectedTheme == DisplayTheme.Dark
                             || selectedTheme == DisplayTheme.System && IsSystemDarkTheme();
#pragma warning disable WPF0001
        Application.Current.ThemeMode = selectedTheme switch
        {
            DisplayTheme.Light => ThemeMode.Light,
            DisplayTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
#pragma warning restore WPF0001

        SetThemeBrush("AppBackgroundBrush", effectiveDarkTheme ? "#191919" : "#F3F3F3");
        SetThemeBrush("PanelBackgroundBrush", effectiveDarkTheme ? "#242424" : "#FAFAFA");
        SetThemeBrush("SurfaceBackgroundBrush", effectiveDarkTheme ? "#202020" : "#FFFFFF");
        SetThemeBrush("PanelBorderBrush", effectiveDarkTheme ? "#3B3B3B" : "#E0E0E0");
        SetThemeBrush("DividerBrush", effectiveDarkTheme ? "#343434" : "#E2E2E2");
        SetThemeBrush("PrimaryTextBrush", effectiveDarkTheme ? "#F2F2F2" : "#202020");
        SetThemeBrush("SecondaryTextBrush", effectiveDarkTheme ? "#BBBBBB" : "#666666");
        SetThemeBrush("TertiaryTextBrush", effectiveDarkTheme ? "#999999" : "#777777");
        SetThemeBrush("BusyOverlayBrush", effectiveDarkTheme ? "#E6202020" : "#DFFFFFFF");

        ThemeButton.Content = selectedTheme switch
        {
            DisplayTheme.Light => "☀",
            DisplayTheme.Dark => "☾",
            _ => "◐"
        };
        ThemeButton.ToolTip = TF("Theme.Tooltip", T(selectedTheme switch
        {
            DisplayTheme.Light => "Theme.Light",
            DisplayTheme.Dark => "Theme.Dark",
            _ => "Theme.System"
        }));
        SendTheme();
    }

    private static void SetThemeBrush(string resourceKey, string colorValue)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorValue)!;
        Application.Current.Resources[resourceKey] = new SolidColorBrush(color);
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("Dialog.OpenTitle"),
            Filter = T("Dialog.TrackFilter"),
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) _ = LoadFilesAsync(dialog.FileNames);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isFullScreen = WindowStyle == WindowStyle.None;
        if (e.Key == Key.F11 || e.Key == Key.Escape && isFullScreen)
        {
            if (Content is not Grid rootGrid || MapView.Parent is not Border mapFrame) return;

            if (!isFullScreen)
            {
                Tag = WindowState;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                rootGrid.RowDefinitions[0].Height = new GridLength(0);
                rootGrid.RowDefinitions[2].Height = new GridLength(0);
                mapFrame.Margin = new Thickness(0);
                mapFrame.BorderThickness = new Thickness(0);
                mapFrame.CornerRadius = new CornerRadius(0);
                WindowState = WindowState.Maximized;
            }
            else
            {
                var previousState = Tag is WindowState state ? state : WindowState.Normal;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                rootGrid.RowDefinitions[0].Height = new GridLength(48);
                rootGrid.RowDefinitions[2].Height = new GridLength(26);
                mapFrame.Margin = new Thickness(8);
                mapFrame.BorderThickness = new Thickness(1);
                mapFrame.CornerRadius = new CornerRadius(10);
                WindowState = previousState;
                Tag = null;
            }

            e.Handled = true;
            return;
        }

        if (e.Key != Key.O || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        OnOpenFile(sender, new RoutedEventArgs());
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFiles(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (TryGetDroppedFiles(e.Data, out var paths)) _ = LoadFilesAsync(paths);
    }

    private static bool TryGetDroppedFiles(IDataObject data, out string[] paths)
    {
        paths = [];
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return false;
        paths = files.Where(IsSupportedTrackFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length > 0;
    }

    private static bool IsSupportedTrackFile(string path) =>
        File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    private async void OnCoordinateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingToolbarSelectors && IsLoaded && openTracks.Count > 0) await ReloadOpenTracksAsync();
    }

    private void OnMapStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingToolbarSelectors && IsLoaded) SendMapStyle();
    }

    private async Task LoadFilesAsync(IEnumerable<string> paths)
    {
        var requestedPaths = paths
            .Where(IsSupportedTrackFile)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedPaths.Length == 0) return;

        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;
        var addedTrack = false;
        var failures = new List<string>();

        for (var index = 0; index < requestedPaths.Length; index++)
        {
            var path = requestedPaths[index];
            var existing = FindOpenTrackByPath(path);
            if (existing is not null)
            {
                activeTrackId = existing.Id;
                continue;
            }

            StatusText.Text = requestedPaths.Length == 1
                ? TF("Status.Opening", Path.GetFileName(path))
                : TF("Status.OpeningBatch", index + 1, requestedPaths.Length, Path.GetFileName(path));
            try
            {
                var options = new TrackLoadOptions { SourceCoordinateSystem = GetSelectedCoordinateSystem() };
                var document = await loader.LoadAsync(path, options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var statistics = TrackStatisticsCalculator.Calculate(document);
                var ordinal = nextTrackOrdinal++;
                var track = new OpenTrackState(
                    $"track-{ordinal}", path, AllocateTrackColor(ordinal), document, statistics);
                var recentEntry = RegisterRecentTrack(path, document, statistics);
                track.PlaceName = recentEntry.PlaceName;
                openTracks.Add(track);
                activeTrackId = track.Id;
                addedTrack = true;
                StartPlaceNameResolution(recentEntry);
            }
            catch (OperationCanceledException)
            {
                UpdateCurrentTrackPresentation();
                if (addedTrack) SendTrackCollection(fit: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                failures.Add(TF("Dialog.FileError", Path.GetFileName(path), exception.Message));
            }
            catch (Exception exception)
            {
                failures.Add(TF("Dialog.UnexpectedReadError", Path.GetFileName(path), exception.Message));
            }
        }

        UpdateCurrentTrackPresentation();
        if (addedTrack) SendTrackCollection(fit: true);
        else SendActiveTrack();
        SendCurrentPlaceName();
        SetRecentPanelVisible(false);

        if (failures.Count == 0) return;
        MessageBox.Show(this, string.Join("\n\n", failures), T("Dialog.PartialOpenTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task ReloadOpenTracksAsync()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;
        var failures = new List<string>();

        for (var index = 0; index < openTracks.Count; index++)
        {
            var track = openTracks[index];
            StatusText.Text = TF("Status.Reloading", index + 1, openTracks.Count, Path.GetFileName(track.Path));
            try
            {
                var options = new TrackLoadOptions { SourceCoordinateSystem = GetSelectedCoordinateSystem() };
                var document = await loader.LoadAsync(track.Path, options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var statistics = TrackStatisticsCalculator.Calculate(document);
                track.Document = document;
                track.Statistics = statistics;
                var recentEntry = RegisterRecentTrack(track.Path, document, statistics);
                track.PlaceName = recentEntry.PlaceName;
                StartPlaceNameResolution(recentEntry);
            }
            catch (OperationCanceledException)
            {
                UpdateCurrentTrackPresentation();
                SendTrackCollection(fit: false);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                failures.Add(TF("Dialog.FileError", Path.GetFileName(track.Path), exception.Message));
            }
            catch (Exception exception)
            {
                failures.Add(TF("Dialog.UnexpectedReloadError", Path.GetFileName(track.Path), exception.Message));
            }
        }

        UpdateCurrentTrackPresentation();
        SendTrackCollection(fit: false);
        SendCurrentPlaceName();
        if (failures.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n\n", failures), T("Dialog.PartialReloadTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private SourceCoordinateSystem GetSelectedCoordinateSystem() =>
        (CoordinateBox.SelectedItem as ComboBoxItem)?.Tag is SourceCoordinateSystem coordinateSystem
            ? coordinateSystem
            : SourceCoordinateSystem.Wgs84;

    private void SendTheme()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setTheme", Theme = effectiveDarkTheme ? "dark" : "light" };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendMapStyle()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var mapStyle = (MapStyleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openfreemap";
        var message = new { Type = "setMapStyle", MapStyle = mapStyle };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendTerrainMode()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setTerrainEnabled", Enabled = terrainEnabled };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void UpdateTerrainButton()
    {
        TerrainButton.Content = terrainEnabled ? "2D" : "3D";
        TerrainButton.ToolTip = T(terrainEnabled ? "Terrain.Disable" : "Terrain.Enable");
    }

    private void SetTerrainState(bool enabled, string? error)
    {
        terrainEnabled = enabled;
        UpdateTerrainButton();
        if (!string.IsNullOrWhiteSpace(error)) StatusText.Text = error;
    }

    private OpenTrackState? CurrentTrack => activeTrackId is null
        ? null
        : openTracks.FirstOrDefault(track => track.Id == activeTrackId);

    private string? currentPath => CurrentTrack?.Path;

    private OpenTrackState? FindOpenTrack(string id) =>
        openTracks.FirstOrDefault(track => track.Id == id);

    private OpenTrackState? FindOpenTrackByPath(string path) =>
        openTracks.FirstOrDefault(track => string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase));

    private string AllocateTrackColor(int ordinal)
    {
        var available = TrackColors.FirstOrDefault(color =>
            openTracks.All(track => !string.Equals(track.Color, color, StringComparison.OrdinalIgnoreCase)));
        if (available is not null) return available;
        var hue = (ordinal * 137.508 + 212) % 360;
        return $"hsl({hue:F0}, 68%, 46%)";
    }

    private void SendTrackCollection(bool fit)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new
        {
            Type = "setTracks",
            Tracks = openTracks.Select(BuildWebTrackPayload).ToArray(),
            ActiveTrackId = activeTrackId,
            Fit = fit
        };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendActiveTrack()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setActiveTrack", Id = activeTrackId };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendTrackVisibility(OpenTrackState track)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setTrackVisibility", track.Id, track.Visible };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendRemovedTrack(string id)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "removeTrack", Id = id, ActiveTrackId = activeTrackId };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SelectTrack(string id)
    {
        var track = FindOpenTrack(id);
        if (track is null) return;
        activeTrackId = track.Id;
        UpdateCurrentTrackPresentation();
        SendActiveTrack();
        SendCurrentPlaceName();
    }

    private void SetTrackVisibility(string id, bool visible)
    {
        var track = FindOpenTrack(id);
        if (track is null || track.Visible == visible) return;
        track.Visible = visible;
        SendTrackVisibility(track);
    }

    private void CloseTrack(string id)
    {
        var index = openTracks.FindIndex(track => track.Id == id);
        if (index < 0) return;
        var wasActive = activeTrackId == id;
        openTracks.RemoveAt(index);
        if (wasActive)
        {
            activeTrackId = openTracks.Count == 0
                ? null
                : openTracks[Math.Min(index, openTracks.Count - 1)].Id;
        }
        UpdateCurrentTrackPresentation();
        SendRemovedTrack(id);
        SendCurrentPlaceName();
        SetRecentPanelVisible(false);
    }

    private void UpdateCurrentTrackPresentation()
    {
        var track = CurrentTrack;
        if (track is null)
        {
            Title = "GpxView";
            StatusText.Text = T("App.Ready");
            StatusSummaryText.Text = string.Empty;
            StatusSummaryText.Visibility = Visibility.Collapsed;
            return;
        }

        Title = $"{track.Document.Name} — GpxView";
        StatusText.Text = track.PlaceName is { Length: > 0 } placeName
            ? $"{Path.GetFileName(track.Path)} · {placeName}"
            : Path.GetFileName(track.Path);
        StatusSummaryText.Text = BuildStatusSummary(track.Document, track.Statistics);
        StatusSummaryText.Visibility = Visibility.Visible;
    }

    private WebTrackPayload BuildWebTrackPayload(OpenTrackState track)
    {
        const int maximumMapPoints = 30_000;
        const int maximumProfilePoints = 8_000;
        var document = track.Document;
        var statistics = track.Statistics;
        var mapStride = Math.Max(1, (int)Math.Ceiling(document.PointCount / (double)maximumMapPoints));
        var profileCandidates = new List<WebPoint>(Math.Min(document.PointCount, maximumProfilePoints * 2));
        var segments = new List<WebSegment>();
        var distanceMeters = 0d;
        var globalIndex = 0;
        var segmentIndex = 0;
        var firstTimestamp = document.Segments.SelectMany(segment => segment.Points)
            .Select(point => point.Timestamp).FirstOrDefault(timestamp => timestamp.HasValue);

        foreach (var segment in document.Segments)
        {
            var coordinates = new List<double[]>();
            TrackPoint? previous = null;
            for (var index = 0; index < segment.Points.Count; index++, globalIndex++)
            {
                var point = segment.Points[index];
                var speedMetersPerSecond = point.SpeedMetersPerSecond;
                if (previous is not null)
                {
                    var stepDistance = TrackStatisticsCalculator.DistanceMeters(previous, point);
                    distanceMeters += stepDistance;
                    if (speedMetersPerSecond is null
                        && previous.Timestamp is { } previousTime
                        && point.Timestamp is { } currentTime)
                    {
                        var seconds = (currentTime - previousTime).TotalSeconds;
                        if (seconds is > 0 and <= 3600) speedMetersPerSecond = stepDistance / seconds;
                    }
                }
                previous = point;

                if (globalIndex % mapStride == 0 || index == segment.Points.Count - 1)
                    coordinates.Add([point.Longitude, point.Latitude]);
                double? elapsedSeconds = firstTimestamp is { } start && point.Timestamp is { } timestamp
                    ? Math.Max(0, (timestamp - start).TotalSeconds)
                    : null;
                profileCandidates.Add(new WebPoint(point.Latitude, point.Longitude, distanceMeters / 1000,
                    point.ElevationMeters, speedMetersPerSecond is { } speed ? speed * 3.6 : null,
                    point.HeartRateBpm, point.CadenceRpm, point.PowerWatts, segmentIndex, elapsedSeconds));
            }
            if (coordinates.Count > 0) segments.Add(new WebSegment(coordinates));
            segmentIndex++;
        }

        var profileStride = Math.Max(1, (int)Math.Ceiling(profileCandidates.Count / (double)maximumProfilePoints));
        var profile = profileCandidates.Where((point, index) =>
            index % profileStride == 0
            || index == 0
            || index == profileCandidates.Count - 1
            || index > 0 && profileCandidates[index - 1].SegmentIndex != point.SegmentIndex
            || index + 1 < profileCandidates.Count && profileCandidates[index + 1].SegmentIndex != point.SegmentIndex).ToArray();
        var waypoints = document.Waypoints.Select(waypoint => new WebWaypoint(
            waypoint.Latitude,
            waypoint.Longitude,
            waypoint.ElevationMeters,
            waypoint.Name,
            waypoint.Comment,
            waypoint.Description,
            waypoint.Symbol,
            waypoint.Type)).ToArray();
        return new WebTrackPayload(track.Id, document.Name, Path.GetFileName(track.Path), track.Color,
            track.Visible, track.PlaceName, segments, waypoints, profile, BuildWebSummary(document, statistics));
    }

    private WebSummary BuildWebSummary(TrackDocument document, TrackStatistics statistics)
    {
        var sensorValues = new List<string>(2);
        if (statistics.AverageCadenceRpm is { } averageCadence) sensorValues.Add($"{averageCadence:N0} rpm");
        if (statistics.AveragePowerWatts is { } averagePower) sensorValues.Add($"{averagePower:N0} W");

        var formatLine = TF("Summary.FormatLine", document.Format.ToString().ToUpperInvariant(),
            statistics.SegmentCount, statistics.PointCount);
        if (document.WaypointCount > 0) formatLine += $" · {TF("Summary.StatusWaypoints", document.WaypointCount)}";

        return new WebSummary(
            formatLine,
            statistics.DistanceMeters >= 1000 ? $"{statistics.DistanceMeters / 1000:N2} km" : $"{statistics.DistanceMeters:N0} m",
            statistics.Duration > TimeSpan.Zero ? $"{FormatDuration(statistics.Duration)} / {FormatDuration(statistics.MovingTime)}" : null,
            statistics.MinimumElevationMeters is not null ? $"↑ {statistics.ElevationGainMeters:N0} m   ↓ {statistics.ElevationLossMeters:N0} m" : null,
            statistics.AverageSpeedMetersPerSecond is { } averageSpeed
                ? $"{averageSpeed * 3.6:N1} / {(statistics.MaximumSpeedMetersPerSecond ?? averageSpeed) * 3.6:N1} km/h" : null,
            statistics.AverageHeartRateBpm is { } averageHeartRate
                ? $"{averageHeartRate:N0} / {statistics.MaximumHeartRateBpm:N0} bpm" : null,
            sensorValues.Count > 0 ? string.Join(" / ", sensorValues) : null);
    }

    private string BuildStatusSummary(TrackDocument document, TrackStatistics statistics)
    {
        var values = new List<string>
        {
            document.Format.ToString().ToUpperInvariant(),
            statistics.DistanceMeters >= 1000
                ? $"{statistics.DistanceMeters / 1000:N2} km"
                : $"{statistics.DistanceMeters:N0} m"
        };
        if (statistics.MinimumElevationMeters is not null) values.Add($"↑ {statistics.ElevationGainMeters:N0} m");
        if (statistics.Duration > TimeSpan.Zero) values.Add(FormatDuration(statistics.Duration));
        values.Add(TF("Summary.StatusPoints", statistics.PointCount));
        if (document.WaypointCount > 0) values.Add(TF("Summary.StatusWaypoints", document.WaypointCount));
        return string.Join("  ·  ", values);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes:00}:{duration.Seconds:00}";

    private static MapServiceOptions LoadMapServiceOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MapServices.local.json");
        if (!File.Exists(path)) return new MapServiceOptions();

        try
        {
            var options = JsonSerializer.Deserialize<MapServiceOptions>(File.ReadAllText(path), JsonOptions)
                          ?? new MapServiceOptions();
            return BuildInfo.SupportsTianditu ? options : options with { Tianditu = null };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MapServiceOptions();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        CloseRoadNetworkServices();
        CloseRecentTrackServices();
    }

    private enum DisplayTheme
    {
        System,
        Light,
        Dark
    }

    private sealed record MapServiceOptions
    {
        public TiandituOptions? Tianditu { get; init; }
    }

    private sealed record TiandituOptions
    {
        public string Tk { get; init; } = string.Empty;
        public string Sk { get; init; } = string.Empty;
    }

    private sealed class OpenTrackState(
        string id,
        string path,
        string color,
        TrackDocument document,
        TrackStatistics statistics)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public string Color { get; } = color;
        public TrackDocument Document { get; set; } = document;
        public TrackStatistics Statistics { get; set; } = statistics;
        public bool Visible { get; set; } = true;
        public string? PlaceName { get; set; }
    }

    private sealed record WebTrackPayload(string Id, string Name, string FileName, string Color, bool Visible,
        string? PlaceName, IReadOnlyList<WebSegment> Segments, IReadOnlyList<WebWaypoint> Waypoints,
        IReadOnlyList<WebPoint> Profile, WebSummary Summary);
    private sealed record WebSummary(string FormatLine, string Distance, string? Duration, string? Elevation,
        string? Speed, string? HeartRate, string? CadencePower);
    private sealed record WebSegment(IReadOnlyList<double[]> Coordinates);
    private sealed record WebWaypoint(double Latitude, double Longitude, double? ElevationMeters,
        string? Name, string? Comment, string? Description, string? Symbol, string? Type);
    private sealed record WebPoint(double Latitude, double Longitude, double DistanceKm, double? ElevationMeters,
        double? SpeedKmh, int? HeartRateBpm, int? CadenceRpm, double? PowerWatts,
        int SegmentIndex, double? ElapsedSeconds);
}

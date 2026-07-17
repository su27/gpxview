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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gpx", ".kml", ".kmz", ".fit"
    };
    private readonly TrackFileLoader loader = new();
    private readonly MapServiceOptions mapServices = LoadMapServiceOptions();
    private CancellationTokenSource? loadCancellation;
    private TrackDocument? currentDocument;
    private TrackStatistics? currentStatistics;
    private string? currentPath;
    private bool webReady;
    private DisplayTheme selectedTheme = DisplayTheme.System;
    private bool effectiveDarkTheme;

    public MainWindow()
    {
        InitializeComponent();
        var tiandituEnabled = mapServices.Tianditu is { Tk.Length: > 0, Sk.Length: > 0 };
        for (var index = 2; index <= 4; index++)
        {
            if (MapStyleBox.Items[index] is ComboBoxItem item)
            {
                item.IsEnabled = tiandituEnabled;
                if (!tiandituEnabled) item.ToolTip = "请配置 MapServices.local.json";
            }
        }
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        ApplyTheme();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webViewDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GpxView", "WebView2");
            var webEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataFolder);
            await MapView.EnsureCoreWebView2Async(webEnvironment);
            var core = MapView.CoreWebView2;
            MapView.AllowExternalDrop = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var mapServicesJson = JsonSerializer.Serialize(mapServices, JsonOptions);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.gpxViewMapServices={mapServicesJson};");
            core.SetVirtualHostNameToFolderMapping(
                "app.gpxview",
                Path.Combine(AppContext.BaseDirectory, "Web"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith("https://app.gpxview/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            MapView.Source = new Uri("https://app.gpxview/index.html");

            var startupPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(IsSupportedTrackFile);
            if (startupPath is not null) await LoadFileAsync(Path.GetFullPath(startupPath));
        }
        catch (Exception exception)
        {
            StatusText.Text = "WebView2 初始化失败，地图暂不可用。";
            MessageBox.Show(this, $"无法初始化地图组件：\n{exception.Message}\n\n请安装或修复 Microsoft Edge WebView2 Runtime。",
                "地图初始化失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.ValueKind == JsonValueKind.String
                && message.RootElement.GetString() == "ready")
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
        ThemeButton.ToolTip = $"主题：{selectedTheme switch
        {
            DisplayTheme.Light => "浅色",
            DisplayTheme.Dark => "深色",
            _ => "跟随系统"
        }}（点击切换）";
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
            Title = "打开轨迹文件",
            Filter = "轨迹文件 (*.gpx;*.kml;*.kmz;*.fit)|*.gpx;*.kml;*.kmz;*.fit|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) _ = LoadFileAsync(dialog.FileName);
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
        e.Effects = TryGetDroppedFile(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (TryGetDroppedFile(e.Data, out var path)) _ = LoadFileAsync(path);
    }

    private static bool TryGetDroppedFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return false;
        path = files[0];
        return IsSupportedTrackFile(path);
    }

    private static bool IsSupportedTrackFile(string path) =>
        File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    private async void OnCoordinateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && currentPath is not null) await LoadFileAsync(currentPath);
    }

    private void OnMapStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) SendMapStyle();
    }

    private async Task LoadFileAsync(string path)
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;
        StatusText.Text = $"正在打开 {Path.GetFileName(path)}";

        try
        {
            var options = new TrackLoadOptions { SourceCoordinateSystem = GetSelectedCoordinateSystem() };
            var document = await loader.LoadAsync(path, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            currentPath = path;
            currentDocument = document;
            var statistics = TrackStatisticsCalculator.Calculate(document);
            currentStatistics = statistics;
            var recentEntry = RegisterRecentTrack(path, document, statistics);
            SendToMap(document, statistics);
            SendCurrentPlaceName();
            Title = $"{document.Name} — GpxView";
            StatusText.Text = Path.GetFileName(path);
            StatusSummaryText.Text = BuildStatusSummary(document, statistics);
            StatusSummaryText.Visibility = Visibility.Visible;
            _ = ResolvePlaceNameAsync(recentEntry, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            StatusText.Text = "文件打开失败";
            MessageBox.Show(this, exception.Message, "无法打开轨迹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            StatusText.Text = "发生未预期的错误";
            MessageBox.Show(this, $"读取轨迹时发生错误：\n{exception.Message}", "GpxView", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private SourceCoordinateSystem GetSelectedCoordinateSystem() => CoordinateBox.SelectedIndex switch
    {
        1 => SourceCoordinateSystem.Gcj02,
        2 => SourceCoordinateSystem.Bd09,
        _ => SourceCoordinateSystem.Wgs84
    };

    private void SendTheme()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setTheme", Theme = effectiveDarkTheme ? "dark" : "light" };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendMapStyle()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var mapStyle = MapStyleBox.SelectedIndex switch
        {
            1 => "osm",
            2 => "tianditu-street",
            3 => "tianditu-imagery",
            4 => "tianditu-terrain",
            5 => "satellite",
            6 => "topo",
            7 => "humanitarian",
            _ => "openfreemap"
        };
        var message = new { Type = "setMapStyle", MapStyle = mapStyle };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendToMap(TrackDocument document, TrackStatistics statistics)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var payload = BuildWebPayload(document, statistics);
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static WebPayload BuildWebPayload(TrackDocument document, TrackStatistics statistics)
    {
        const int maximumMapPoints = 30_000;
        const int maximumProfilePoints = 8_000;
        var mapStride = Math.Max(1, (int)Math.Ceiling(document.PointCount / (double)maximumMapPoints));
        var profileCandidates = new List<WebPoint>(Math.Min(document.PointCount, maximumProfilePoints * 2));
        var segments = new List<WebSegment>();
        var distanceMeters = 0d;
        var globalIndex = 0;

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
                profileCandidates.Add(new WebPoint(point.Latitude, point.Longitude, distanceMeters / 1000,
                    point.ElevationMeters, speedMetersPerSecond is { } speed ? speed * 3.6 : null,
                    point.HeartRateBpm, point.CadenceRpm, point.PowerWatts));
            }
            if (coordinates.Count > 0) segments.Add(new WebSegment(coordinates));
        }

        var profileStride = Math.Max(1, (int)Math.Ceiling(profileCandidates.Count / (double)maximumProfilePoints));
        var profile = profileCandidates.Where((_, index) => index % profileStride == 0 || index == profileCandidates.Count - 1).ToArray();
        return new WebPayload("loadTrack", document.Name, segments, profile, BuildWebSummary(document, statistics));
    }

    private static WebSummary BuildWebSummary(TrackDocument document, TrackStatistics statistics)
    {
        var sensorValues = new List<string>(2);
        if (statistics.AverageCadenceRpm is { } averageCadence) sensorValues.Add($"{averageCadence:N0} rpm");
        if (statistics.AveragePowerWatts is { } averagePower) sensorValues.Add($"{averagePower:N0} W");

        return new WebSummary(
            $"{document.Format.ToString().ToUpperInvariant()} · {statistics.SegmentCount} 分段 · {statistics.PointCount:N0} 轨迹点",
            statistics.DistanceMeters >= 1000 ? $"{statistics.DistanceMeters / 1000:N2} km" : $"{statistics.DistanceMeters:N0} m",
            statistics.Duration > TimeSpan.Zero ? $"{FormatDuration(statistics.Duration)} / {FormatDuration(statistics.MovingTime)}" : null,
            statistics.MinimumElevationMeters is not null ? $"↑ {statistics.ElevationGainMeters:N0} m   ↓ {statistics.ElevationLossMeters:N0} m" : null,
            statistics.AverageSpeedMetersPerSecond is { } averageSpeed
                ? $"{averageSpeed * 3.6:N1} / {(statistics.MaximumSpeedMetersPerSecond ?? averageSpeed) * 3.6:N1} km/h" : null,
            statistics.AverageHeartRateBpm is { } averageHeartRate
                ? $"{averageHeartRate:N0} / {statistics.MaximumHeartRateBpm:N0} bpm" : null,
            sensorValues.Count > 0 ? string.Join(" / ", sensorValues) : null);
    }

    private static string BuildStatusSummary(TrackDocument document, TrackStatistics statistics)
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
        values.Add($"{statistics.PointCount:N0} 点");
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
            return JsonSerializer.Deserialize<MapServiceOptions>(File.ReadAllText(path), JsonOptions)
                   ?? new MapServiceOptions();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MapServiceOptions();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
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
        public GeocodingOptions? Geocoding { get; init; }
    }

    private sealed record TiandituOptions
    {
        public string Tk { get; init; } = string.Empty;
        public string Sk { get; init; } = string.Empty;
    }

    private sealed record GeocodingOptions
    {
        public bool Enabled { get; init; } = true;
        public string Endpoint { get; init; } = DefaultGeocodingEndpoint;
    }

    private sealed record WebPayload(string Type, string Name, IReadOnlyList<WebSegment> Segments,
        IReadOnlyList<WebPoint> Profile, WebSummary Summary);
    private sealed record WebSummary(string FormatLine, string Distance, string? Duration, string? Elevation,
        string? Speed, string? HeartRate, string? CadencePower);
    private sealed record WebSegment(IReadOnlyList<double[]> Coordinates);
    private sealed record WebPoint(double Latitude, double Longitude, double DistanceKm, double? ElevationMeters,
        double? SpeedKmh, int? HeartRateBpm, int? CadenceRpm, double? PowerWatts);
}

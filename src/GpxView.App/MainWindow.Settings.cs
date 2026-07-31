using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GpxView.Core;

namespace GpxView.App;

public partial class MainWindow
{
    private static readonly MapStyleDefinition[] MapStyles =
    [
        new("openfreemap", "Map.Modern", "Map.ModernTooltip"),
        new("outdoor", "Map.Outdoor", "Map.OutdoorTooltip"),
        new("osm", "Map.Classic", "Map.ClassicTooltip"),
        new("tianditu-street", "Map.Street", "Map.StreetTooltip", true),
        new("tianditu-imagery", "Map.Imagery", "Map.ImageryTooltip", true),
        new("tianditu-terrain", "Map.Terrain", "Map.TerrainTooltip", true),
        new("satellite", "Map.Satellite", "Map.SatelliteTooltip"),
        new("topo", "Map.Topo", "Map.TopoTooltip"),
        new("humanitarian", "Map.Humanitarian", "Map.HumanitarianTooltip")
    ];

    private readonly AppSettingsStore appSettingsStore = new();
    private AppSettings appSettings = new();
    private LocalizationCatalog localization = LocalizationCatalog.Create("system");
    private bool updatingToolbarSelectors;

    private string T(string key) => localization.Get(key);
    private string TF(string key, params object?[] values) => localization.Format(key, values);

    private void InitializeApplicationSettings()
    {
        appSettings = appSettingsStore.Load();
        localization = LocalizationCatalog.Create(appSettings.Language);
    }

    private void PopulateToolbarSelectors()
    {
        updatingToolbarSelectors = true;
        try
        {
            var selectedMapId = (MapStyleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openfreemap";
            MapStyleBox.Items.Clear();
            var tiandituConfigured = mapServices.Tianditu is { Tk.Length: > 0, Sk.Length: > 0 };
            foreach (var definition in MapStyles.Where(definition =>
                         !definition.RequiresTianditu || BuildInfo.SupportsTianditu))
            {
                var item = new ComboBoxItem
                {
                    Tag = definition.Id,
                    Content = T(definition.LabelKey),
                    ToolTip = definition.RequiresTianditu && !tiandituConfigured
                        ? T("Map.TiandituMissing")
                        : T(definition.TooltipKey),
                    IsEnabled = !definition.RequiresTianditu || tiandituConfigured
                };
                MapStyleBox.Items.Add(item);
            }
            MapStyleBox.SelectedItem = MapStyleBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedMapId, StringComparison.Ordinal))
                ?? MapStyleBox.Items[0];

            var coordinateSystem = GetSelectedCoordinateSystem();
            CoordinateBox.Items.Clear();
            CoordinateBox.Items.Add(new ComboBoxItem
                { Tag = SourceCoordinateSystem.Wgs84, Content = T("Coordinates.Wgs84") });
            CoordinateBox.Items.Add(new ComboBoxItem
                { Tag = SourceCoordinateSystem.Gcj02, Content = T("Coordinates.Gcj02") });
            CoordinateBox.Items.Add(new ComboBoxItem
                { Tag = SourceCoordinateSystem.Bd09, Content = T("Coordinates.Bd09") });
            CoordinateBox.SelectedItem = CoordinateBox.Items.Cast<ComboBoxItem>()
                .First(item => item.Tag is SourceCoordinateSystem value && value == coordinateSystem);
        }
        finally
        {
            updatingToolbarSelectors = false;
        }
    }

    private void ApplyLocalization()
    {
        OpenButtonText.Text = T("Toolbar.Open");
        OpenButton.ToolTip = T("Toolbar.OpenTooltip");
        AutomationProperties.SetName(OpenButton, T("Toolbar.Open"));
        RecentButtonText.Text = T("Toolbar.Recent");
        RecentButton.ToolTip = T("Toolbar.RecentTooltip");
        AutomationProperties.SetName(RecentButton, T("Toolbar.Recent"));
        MapStyleIcon.ToolTip = T("Toolbar.BaseMap");
        CoordinateIcon.ToolTip = T("Toolbar.Coordinates");
        RoadNetworkButton.Content = T("Toolbar.RoadNetwork");
        SettingsButton.ToolTip = T("Toolbar.Settings");
        AutomationProperties.SetName(SettingsButton, T("Toolbar.Settings"));
        PopulateToolbarSelectors();
        UpdateTerrainButton();
        RefreshRoadNetworkArchives(notifyWeb: false);
        UpdateCurrentTrackPresentation();

        if (!webReady) return;
        SendLocalization();
        SendRoadNetworkConfig();
        SendTrackCollection(fit: false);
        SendRecentTracks();
        SendSettingsState();
        SendCurrentPlaceName();
    }

    private void SendLocalization()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new
        {
            Type = "setLocalization",
            localization.Locale,
            Strings = localization.Export()
        };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        RefreshRoadNetworkArchives();
        SendSettingsState();
        SetSettingsPanelVisible(true);
    }

    private void SetSettingsPanelVisible(bool visible)
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setSettingsPanelVisible", Visible = visible };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendSettingsState()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new
        {
            Type = "setSettings",
            Language = appSettings.Language,
            ResolvedLocale = localization.Locale,
            GeocodingAvailable = IsGeocodingAvailable,
            GeocodingEnabled = IsGeocodingEnabled,
            GeocodingConsentPending = IsGeocodingAvailable && appSettings.GeocodingEnabled is null,
            BuildInfo.Version,
            BuildInfo.Channel,
            FileAssociations = FileAssociationManager.GetStatuses(),
            RoadNetworks = BuildRoadNetworkSettingsPayload(),
            RoadNetworkCache = BuildRoadNetworkCacheSettingsPayload()
        };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SetLanguage(string language)
    {
        if (language is not ("system" or "zh-CN" or "en-US") || appSettings.Language == language) return;
        appSettings = appSettings with { Language = language };
        appSettingsStore.Save(appSettings);
        localization = LocalizationCatalog.Create(language);
        ResetReverseGeocoder();
        ApplyLocalization();
    }

    private void AssociateFileType(string extension)
    {
        var result = FileAssociationManager.Associate(extension);
        SendSettingsState();
        StatusText.Text = result.Status switch
        {
            FileAssociationUpdateStatus.Associated => TF(
                "Status.FileAssociationUpdated",
                result.Extension.ToUpperInvariant()),
            FileAssociationUpdateStatus.NeedsSystemConfirmation => TF(
                "Status.FileAssociationNeedsConfirmation",
                result.Extension.ToUpperInvariant()),
            _ => TF("Status.FileAssociationFailed", result.Extension.ToUpperInvariant())
        };
    }

    private void OpenDefaultAppsSettings()
    {
        if (TryStartShell("ms-settings:defaultapps?registeredAppUser=GpxView")
            || TryStartShell("ms-settings:defaultapps")) return;
        StatusText.Text = T("Status.DefaultAppsFailed");
    }

    private void OpenRoadNetworkFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RoadNetworkFolder);
            if (TryStartShell(AppPaths.RoadNetworkFolder)) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The status bar below keeps the settings panel usable when Explorer cannot open the folder.
        }
        StatusText.Text = T("Status.RoadFolderFailed");
    }

    private void OpenProjectHome()
    {
        if (!TryStartShell("https://github.com/su27/gpxview"))
            StatusText.Text = T("Status.ProjectHomeFailed");
    }

    private static bool TryStartShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                               or System.ComponentModel.Win32Exception
                                               or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadString(JsonElement message, string propertyName, out string value)
    {
        value = string.Empty;
        if (!message.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private sealed record MapStyleDefinition(
        string Id,
        string LabelKey,
        string TooltipKey,
        bool RequiresTianditu = false);
}

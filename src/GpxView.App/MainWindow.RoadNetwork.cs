using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using GpxView.Core;
using Microsoft.Web.WebView2.Core;

namespace GpxView.App;

public partial class MainWindow
{
    private const string RoadNetworkRequestBaseUrl = "https://roadnet.gpxview/archives/";
    private readonly List<LocalRoadNetworkArchive> roadNetworkArchives = [];
    private readonly Dictionary<string, PmTilesArchive> roadNetworkArchivesByPath =
        new(StringComparer.Ordinal);
    private bool roadNetworkInitialized;
    private bool roadNetworkEnabled;

    private void InitializeRoadNetwork() => RefreshRoadNetworkArchives(notifyWeb: false);

    private void RefreshRoadNetworkArchives(bool notifyWeb = true)
    {
        var wasAvailable = roadNetworkArchives.Count > 0;
        var wasEnabled = roadNetworkEnabled;
        roadNetworkArchives.Clear();
        roadNetworkArchivesByPath.Clear();

        foreach (var archive in PmTilesArchive.Discover(AppPaths.RoadNetworkFolder))
        {
            var id = $"local-{roadNetworkArchives.Count}";
            var requestPath = $"/archives/{id}";
            roadNetworkArchives.Add(new LocalRoadNetworkArchive(
                id,
                GetRoadNetworkDisplayName(archive.Path),
                archive));
            roadNetworkArchivesByPath.Add(requestPath, archive);
        }

        var available = roadNetworkArchives.Count > 0;
        roadNetworkEnabled = available && (!roadNetworkInitialized || !wasAvailable || wasEnabled);
        roadNetworkInitialized = true;
        UpdateRoadNetworkButton();
        if (!notifyWeb) return;
        SendRoadNetworkConfig();
        SendRoadNetworkMode();
    }

    private object GetRoadNetworkWebConfig()
    {
        var archives = roadNetworkArchives
            .Select(entry => new
            {
                entry.Id,
                entry.Name,
                Url = $"{RoadNetworkRequestBaseUrl}{entry.Id}",
                entry.Archive.MinZoom,
                entry.Archive.MaxZoom,
                TileSize = 256,
                Bounds = new[]
                {
                    entry.Archive.West,
                    entry.Archive.South,
                    entry.Archive.East,
                    entry.Archive.North
                }
            })
            .ToArray();
        var bounds = archives.Length == 0
            ? null
            : new[]
            {
                archives.Min(entry => entry.Bounds[0]),
                archives.Min(entry => entry.Bounds[1]),
                archives.Max(entry => entry.Bounds[2]),
                archives.Max(entry => entry.Bounds[3])
            };
        return new
        {
            Available = archives.Length > 0,
            Enabled = roadNetworkEnabled,
            Archives = archives,
            Bounds = bounds
        };
    }

    private object[] BuildRoadNetworkSettingsPayload() => roadNetworkArchives.Select(entry => (object)new
    {
        entry.Name,
        FileName = Path.GetFileName(entry.Archive.Path),
        Bytes = entry.Archive.Length,
        entry.Archive.MinZoom,
        entry.Archive.MaxZoom,
        Bounds = new[]
        {
            entry.Archive.West,
            entry.Archive.South,
            entry.Archive.East,
            entry.Archive.North
        }
    }).ToArray();

    private void ConfigureRoadNetworkRequests(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("https://roadnet.gpxview/archives/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRoadNetworkResourceRequested;
    }

    private void OnRoadNetworkResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        var core = MapView.CoreWebView2;
        if (core is null) return;

        var request = eventArgs.Request;
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri)
            || !roadNetworkArchivesByPath.TryGetValue(uri.AbsolutePath, out var archive))
        {
            eventArgs.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null, 404, "Not Found", CorsHeaders("Content-Length: 0"));
            return;
        }

        if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            eventArgs.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null, 204, "No Content",
                CorsHeaders("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\nAccess-Control-Allow-Headers: Range\r\nContent-Length: 0"));
            return;
        }

        var rangeHeader = request.Headers
            .FirstOrDefault(header => string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (!archive.TryReadRange(rangeHeader, out var content, out var start, out var end))
        {
            eventArgs.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null, 416, "Range Not Satisfiable",
                CorsHeaders($"Accept-Ranges: bytes\r\nContent-Range: bytes */{archive.Length}\r\nContent-Length: 0\r\nETag: {archive.ETag}"));
            return;
        }

        var statusCode = string.IsNullOrWhiteSpace(rangeHeader) ? 200 : 206;
        var reason = statusCode == 206 ? "Partial Content" : "OK";
        var headers = new StringBuilder()
            .AppendLine("Content-Type: application/vnd.pmtiles")
            .AppendLine("Accept-Ranges: bytes")
            .AppendLine($"Content-Length: {content.Length}")
            .AppendLine($"ETag: {archive.ETag}");
        if (statusCode == 206) headers.AppendLine($"Content-Range: bytes {start}-{end}/{archive.Length}");
        eventArgs.Response = core.Environment.CreateWebResourceResponse(
            new MemoryStream(content, writable: false), statusCode, reason, CorsHeaders(headers.ToString()));
    }

    private static string CorsHeaders(string headers) =>
        $"Access-Control-Allow-Origin: *\r\nAccess-Control-Expose-Headers: Accept-Ranges, Content-Length, Content-Range, ETag\r\nCache-Control: no-cache\r\n{headers}";

    private void OnRoadNetworkToggle(object sender, RoutedEventArgs eventArgs)
    {
        if (roadNetworkArchives.Count == 0) return;
        roadNetworkEnabled = !roadNetworkEnabled;
        UpdateRoadNetworkButton();
        SendRoadNetworkMode();
    }

    private void SendRoadNetworkMode()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setRoadNetworkEnabled", Enabled = roadNetworkEnabled };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void SendRoadNetworkConfig()
    {
        if (!webReady || MapView.CoreWebView2 is null) return;
        var message = new { Type = "setRoadNetworkConfig", Config = GetRoadNetworkWebConfig() };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void UpdateRoadNetworkButton()
    {
        var available = roadNetworkArchives.Count > 0;
        RoadNetworkButton.IsEnabled = available;
        RoadNetworkButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        RoadNetworkButton.FontWeight = roadNetworkEnabled ? FontWeights.SemiBold : FontWeights.Normal;
        RoadNetworkButton.Opacity = roadNetworkEnabled ? 1 : 0.62;
        RoadNetworkButton.ToolTip = roadNetworkEnabled
            ? TF("RoadNetwork.HideOne", RoadNetworkDescription())
            : TF("RoadNetwork.ShowOne", RoadNetworkDescription());
    }

    private string RoadNetworkDescription() => roadNetworkArchives.Count == 1
        ? roadNetworkArchives[0].Name
        : TF("RoadNetwork.Count", roadNetworkArchives.Count);

    private string GetRoadNetworkDisplayName(string path) =>
        Path.GetFileNameWithoutExtension(path) switch
        {
            "beijing-density" => T("RoadNetwork.Beijing"),
            "mentougou-density" => T("RoadNetwork.Mentougou"),
            var fileName => fileName
        };

    private void CloseRoadNetworkServices()
    {
        if (MapView.CoreWebView2 is not { } core) return;
        core.WebResourceRequested -= OnRoadNetworkResourceRequested;
    }

    private sealed record LocalRoadNetworkArchive(string Id, string Name, PmTilesArchive Archive);
}

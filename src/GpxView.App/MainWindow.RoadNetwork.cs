using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using GpxView.Core;
using Microsoft.Web.WebView2.Core;

namespace GpxView.App;

public partial class MainWindow
{
    private const string RoadNetworkRequestBaseUrl = "https://roadnet.gpxview/archives/";
    private static readonly Regex RemoteArchiveIdPattern = new("^[a-z0-9-]{1,80}$", RegexOptions.CultureInvariant);
    private readonly List<LocalRoadNetworkArchive> roadNetworkArchives = [];
    private readonly List<RemoteRoadNetworkArchive> remoteRoadNetworkArchives = [];
    private readonly List<RemoteRoadNetworkArchive> remoteRoadNetworkCatalog = [];
    private readonly Dictionary<string, PmTilesArchive> roadNetworkArchivesByPath =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteRoadNetworkArchive> remoteRoadNetworkArchivesByPath =
        new(StringComparer.Ordinal);
    private readonly Lazy<IRoadNetworkCredentialStore> roadNetworkCredentialStore =
        new(() => new WindowsCredentialLockerRoadNetworkStore());
    private readonly SemaphoreSlim remoteRoadNetworkGate = new(1, 1);
    private readonly RoadNetworkRangeCache roadNetworkRangeCache = new(AppPaths.RoadNetworkCacheFolder);
    private RoadNetworkServiceClient? roadNetworkServiceClient;
    private string remoteRoadNetworkStatus = "disconnected";
    private string remoteRoadNetworkError = string.Empty;
    private bool remoteRoadNetworkBusy;
    private bool roadNetworkCacheBusy;
    private bool roadNetworkInitialized;
    private bool roadNetworkEnabled;

    private void InitializeRoadNetwork()
    {
        RefreshRoadNetworkArchives(notifyWeb: false);
        InitializeRoadNetworkServiceClient();
    }

    private void InitializeRoadNetworkServiceClient()
    {
        if (string.IsNullOrWhiteSpace(appSettings.RoadNetworkServiceEndpoint)) return;
        if (!RoadNetworkServiceClient.TryNormalizeEndpoint(
                appSettings.RoadNetworkServiceEndpoint, out var endpoint))
        {
            remoteRoadNetworkStatus = "invalidEndpoint";
            return;
        }

        try
        {
            var token = roadNetworkCredentialStore.Value.ReadDeviceToken(endpoint);
            if (string.IsNullOrWhiteSpace(token)) return;
            roadNetworkServiceClient = new RoadNetworkServiceClient(endpoint, token);
            remoteRoadNetworkStatus = "connecting";
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                               or UnauthorizedAccessException
                                               or System.ComponentModel.Win32Exception
                                               or System.Runtime.InteropServices.COMException)
        {
            remoteRoadNetworkStatus = "credentialError";
            remoteRoadNetworkError = exception.HResult.ToString("X8");
        }
    }

    private void RefreshRoadNetworkArchives(bool notifyWeb = true)
    {
        var wasAvailable = HasRoadNetworkArchives;
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

        RebuildActiveRemoteRoadNetworkArchives();
        ReconcileRoadNetworkAvailability(wasAvailable, wasEnabled);
        if (!notifyWeb) return;
        SendRoadNetworkConfig();
        SendRoadNetworkMode();
    }

    private bool HasRoadNetworkArchives => roadNetworkArchives.Count > 0 || remoteRoadNetworkArchives.Count > 0;

    private void ReconcileRoadNetworkAvailability(bool wasAvailable, bool wasEnabled)
    {
        var available = HasRoadNetworkArchives;
        roadNetworkEnabled = available && (!roadNetworkInitialized || !wasAvailable || wasEnabled);
        roadNetworkInitialized = true;
        UpdateRoadNetworkButton();
    }

    private object GetRoadNetworkWebConfig()
    {
        var localArchives = roadNetworkArchives.Select(entry => new RoadNetworkWebArchive(
            entry.Id,
            entry.Name,
            $"{RoadNetworkRequestBaseUrl}{entry.Id}",
            entry.Archive.MinZoom,
            entry.Archive.MaxZoom,
            256,
            [entry.Archive.West, entry.Archive.South, entry.Archive.East, entry.Archive.North]));
        var remoteArchives = remoteRoadNetworkArchives.Select(entry => new RoadNetworkWebArchive(
            entry.RequestId,
            GetRemoteRoadNetworkDisplayName(entry),
            $"{RoadNetworkRequestBaseUrl}{entry.RequestId}",
            entry.MinZoom,
            entry.MaxZoom,
            entry.TileSize,
            entry.Bounds));
        var archives = localArchives.Concat(remoteArchives).ToArray();
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

    private object BuildOnlineRoadNetworkSettingsPayload() => new
    {
        Endpoint = appSettings.RoadNetworkServiceEndpoint,
        Status = remoteRoadNetworkStatus,
        Error = remoteRoadNetworkError,
        Busy = remoteRoadNetworkBusy,
        Connected = roadNetworkServiceClient?.HasDeviceToken == true
                    && remoteRoadNetworkStatus == "connected",
        Enrolled = roadNetworkServiceClient?.HasDeviceToken == true,
        AccountId = appSettings.RoadNetworkAccountId,
        DisplayName = appSettings.RoadNetworkDisplayName,
        DeviceId = appSettings.RoadNetworkDeviceId,
        Archives = remoteRoadNetworkCatalog.Select(entry => new
        {
            Name = GetRemoteRoadNetworkDisplayName(entry),
            entry.Bytes,
            entry.MinZoom,
            entry.MaxZoom,
            entry.Bounds
        }).ToArray()
    };

    private object BuildRoadNetworkCacheSettingsPayload()
    {
        var stats = roadNetworkRangeCache.GetStats();
        return new
        {
            Visible = stats.Entries > 0
                      || !string.IsNullOrWhiteSpace(appSettings.RoadNetworkServiceEndpoint)
                      || roadNetworkServiceClient?.HasDeviceToken == true,
            stats.Bytes,
            stats.Entries,
            Busy = roadNetworkCacheBusy
        };
    }

    private void ConfigureRoadNetworkRequests(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("https://roadnet.gpxview/archives/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRoadNetworkResourceRequested;
    }

    private async void OnRoadNetworkResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        var deferral = eventArgs.GetDeferral();
        var core = MapView.CoreWebView2;
        if (core is null)
        {
            deferral.Complete();
            return;
        }

        try
        {
            var request = eventArgs.Request;
            if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri))
            {
                eventArgs.Response = EmptyRoadNetworkResponse(core, 400, "Bad Request");
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
            if (roadNetworkArchivesByPath.TryGetValue(uri.AbsolutePath, out var localArchive))
            {
                HandleLocalRoadNetworkRequest(core, eventArgs, localArchive, request.Method, rangeHeader);
                return;
            }
            if (!remoteRoadNetworkArchivesByPath.TryGetValue(uri.AbsolutePath, out var remoteArchive)
                || roadNetworkServiceClient is not { } serviceClient)
            {
                eventArgs.Response = EmptyRoadNetworkResponse(core, 404, "Not Found");
                return;
            }

            var isGet = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);
            long rangeStart = 0;
            long rangeEnd = 0;
            var hasCacheableRange = isGet
                                    && TryParseRoadNetworkRange(
                                        rangeHeader,
                                        remoteArchive.Bytes,
                                        out rangeStart,
                                        out rangeEnd);
            if (hasCacheableRange
                && roadNetworkRangeCache.TryRead(
                    remoteArchive.CacheKey,
                    remoteArchive.ETag,
                    rangeStart,
                    rangeEnd,
                    out var cachedContent))
            {
                eventArgs.Response = CreateCachedRemoteRoadNetworkResponse(
                    core,
                    remoteArchive,
                    cachedContent,
                    rangeStart,
                    rangeEnd);
                return;
            }

            var response = await serviceClient.ReadArchiveAsync(
                remoteArchive.ServicePath,
                request.Method,
                rangeHeader,
                lifetimeCancellation.Token);
            if (hasCacheableRange && IsCacheableRemoteRoadNetworkResponse(response, remoteArchive, rangeStart, rangeEnd))
            {
                roadNetworkRangeCache.TryWrite(
                    remoteArchive.CacheKey,
                    remoteArchive.ETag,
                    rangeStart,
                    rangeEnd,
                    response.Content);
            }
            eventArgs.Response = CreateRemoteRoadNetworkResponse(core, response, request.Method);
        }
        catch (OperationCanceledException)
        {
            eventArgs.Response = EmptyRoadNetworkResponse(core, 499, "Client Closed Request");
        }
        catch (Exception exception) when (exception is HttpRequestException
                                               or IOException
                                               or RoadNetworkServiceException)
        {
            eventArgs.Response = EmptyRoadNetworkResponse(core, 502, "Bad Gateway");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void HandleLocalRoadNetworkRequest(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs eventArgs,
        PmTilesArchive archive,
        string method,
        string? rangeHeader)
    {
        var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        if (isHead && string.IsNullOrWhiteSpace(rangeHeader))
        {
            var headHeaders = new StringBuilder()
                .AppendLine("Content-Type: application/vnd.pmtiles")
                .AppendLine("Accept-Ranges: bytes")
                .AppendLine($"Content-Length: {archive.Length}")
                .AppendLine($"ETag: {archive.ETag}");
            eventArgs.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null, 200, "OK", CorsHeaders(headHeaders.ToString()));
            return;
        }

        if (!archive.TryReadRange(rangeHeader, out var content, out var start, out var end))
        {
            eventArgs.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null, 416, "Range Not Satisfiable",
                CorsHeaders($"Accept-Ranges: bytes\r\nContent-Range: bytes */{archive.Length}\r\nContent-Length: 0\r\nETag: {archive.ETag}"));
            return;
        }

        var statusCode = string.IsNullOrWhiteSpace(rangeHeader) ? 200 : 206;
        var reason = statusCode == 206 ? "Partial Content" : "OK";
        var contentLength = content.Length;
        var headers = new StringBuilder()
            .AppendLine("Content-Type: application/vnd.pmtiles")
            .AppendLine("Accept-Ranges: bytes")
            .AppendLine($"Content-Length: {contentLength}")
            .AppendLine($"ETag: {archive.ETag}");
        if (statusCode == 206) headers.AppendLine($"Content-Range: bytes {start}-{end}/{archive.Length}");
        var body = isHead
            ? Stream.Null
            : new MemoryStream(content, writable: false);
        eventArgs.Response = core.Environment.CreateWebResourceResponse(
            body, statusCode, reason, CorsHeaders(headers.ToString()));
    }

    private static CoreWebView2WebResourceResponse CreateRemoteRoadNetworkResponse(
        CoreWebView2 core,
        RoadNetworkArchiveResponse response,
        string method)
    {
        var headers = new StringBuilder();
        AppendResponseHeader(headers, "Content-Type", response.ContentType ?? "application/vnd.pmtiles");
        AppendResponseHeader(headers, "Accept-Ranges", response.AcceptRanges);
        AppendResponseHeader(headers, "Content-Range", response.ContentRange);
        AppendResponseHeader(headers, "ETag", response.ETag);
        var contentLength = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            ? response.ContentLength ?? 0
            : response.Content.LongLength;
        AppendResponseHeader(headers, "Content-Length", contentLength.ToString());
        var body = response.Content.Length == 0
            ? Stream.Null
            : new MemoryStream(response.Content, writable: false);
        return core.Environment.CreateWebResourceResponse(
            body,
            response.StatusCode,
            response.ReasonPhrase,
            CorsHeaders(headers.ToString()));
    }

    private static CoreWebView2WebResourceResponse CreateCachedRemoteRoadNetworkResponse(
        CoreWebView2 core,
        RemoteRoadNetworkArchive archive,
        byte[] content,
        long start,
        long end)
    {
        var headers = new StringBuilder()
            .AppendLine("Content-Type: application/vnd.pmtiles")
            .AppendLine("Accept-Ranges: bytes")
            .AppendLine($"Content-Range: bytes {start}-{end}/{archive.Bytes}")
            .AppendLine($"Content-Length: {content.LongLength}");
        AppendResponseHeader(headers, "ETag", archive.ETag);
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(content, writable: false),
            206,
            "Partial Content",
            CorsHeaders(headers.ToString()));
    }

    private static bool IsCacheableRemoteRoadNetworkResponse(
        RoadNetworkArchiveResponse response,
        RemoteRoadNetworkArchive archive,
        long start,
        long end)
    {
        if (response.StatusCode != 206 || response.Content.LongLength != end - start + 1) return false;
        if (!string.IsNullOrWhiteSpace(response.ETag)
            && !string.Equals(NormalizeETag(response.ETag), NormalizeETag(archive.ETag), StringComparison.Ordinal))
            return false;
        var expectedContentRange = $"bytes {start}-{end}/{archive.Bytes}";
        return string.IsNullOrWhiteSpace(response.ContentRange)
               || string.Equals(response.ContentRange, expectedContentRange, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeETag(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
    }

    private static bool TryParseRoadNetworkRange(
        string? rangeHeader,
        long archiveLength,
        out long start,
        out long end)
    {
        start = 0;
        end = archiveLength - 1;
        const string prefix = "bytes=";
        if (archiveLength <= 0
            || string.IsNullOrWhiteSpace(rangeHeader)
            || !rangeHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var range = rangeHeader[prefix.Length..];
        if (range.Contains(',')) return false;
        var separator = range.IndexOf('-');
        if (separator <= 0 || !long.TryParse(range[..separator], out start)) return false;
        if (separator + 1 < range.Length && !long.TryParse(range[(separator + 1)..], out end)) return false;
        if (start < 0 || start >= archiveLength) return false;
        end = Math.Min(end, archiveLength - 1);
        return end >= start;
    }

    private static void AppendResponseHeader(StringBuilder headers, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n')) return;
        headers.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static CoreWebView2WebResourceResponse EmptyRoadNetworkResponse(
        CoreWebView2 core,
        int statusCode,
        string reason) => core.Environment.CreateWebResourceResponse(
        Stream.Null, statusCode, reason, CorsHeaders("Content-Length: 0"));

    private static string CorsHeaders(string headers) =>
        $"Access-Control-Allow-Origin: *\r\nAccess-Control-Expose-Headers: Accept-Ranges, Content-Length, Content-Range, ETag\r\nCache-Control: no-cache\r\n{headers}";

    private async Task RefreshRemoteRoadNetworkAsync(bool notifyWeb = true)
    {
        try
        {
            await remoteRoadNetworkGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (roadNetworkServiceClient is not { HasDeviceToken: true } serviceClient)
            {
                remoteRoadNetworkStatus = "disconnected";
                ClearRemoteRoadNetworkArchives(notifyWeb);
                if (notifyWeb) SendSettingsState();
                return;
            }

            remoteRoadNetworkBusy = true;
            remoteRoadNetworkStatus = "connecting";
            remoteRoadNetworkError = string.Empty;
            if (notifyWeb) SendSettingsState();
            try
            {
                var catalog = await serviceClient.GetCatalogAsync(lifetimeCancellation.Token);
                ApplyRemoteRoadNetworkCatalog(serviceClient.Endpoint, catalog, notifyWeb);
                remoteRoadNetworkStatus = "connected";
            }
            catch (Exception exception) when (exception is HttpRequestException
                                                   or IOException
                                                   or TaskCanceledException
                                                   or RoadNetworkServiceException)
            {
                remoteRoadNetworkStatus = exception is RoadNetworkServiceException
                    { StatusCode: HttpStatusCode.Unauthorized }
                    ? "authenticationFailed"
                    : "error";
                remoteRoadNetworkError = exception is RoadNetworkServiceException serviceException
                    ? serviceException.Code
                    : exception.GetType().Name;
            }
            finally
            {
                remoteRoadNetworkBusy = false;
                if (notifyWeb) SendSettingsState();
            }
        }
        finally
        {
            remoteRoadNetworkGate.Release();
        }
    }

    private async Task ConnectRoadNetworkServiceAsync(string endpointText, string enrollmentCode)
    {
        try
        {
            await remoteRoadNetworkGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            remoteRoadNetworkBusy = true;
            remoteRoadNetworkError = string.Empty;
            if (!RoadNetworkServiceClient.TryNormalizeEndpoint(endpointText, out var endpoint))
            {
                remoteRoadNetworkStatus = "invalidEndpoint";
                return;
            }
            appSettings = appSettings with { RoadNetworkServiceEndpoint = endpoint.AbsoluteUri };
            appSettingsStore.Save(appSettings);
            if (string.IsNullOrWhiteSpace(enrollmentCode))
            {
                remoteRoadNetworkStatus = "invalidCode";
                return;
            }

            remoteRoadNetworkStatus = "connecting";
            SendSettingsState();
            var nextClient = new RoadNetworkServiceClient(endpoint);
            try
            {
                var enrollment = await nextClient.EnrollAsync(
                    enrollmentCode.Trim(),
                    Environment.MachineName,
                    BuildInfo.Version,
                    lifetimeCancellation.Token);
                roadNetworkCredentialStore.Value.SaveDeviceToken(endpoint, enrollment.DeviceToken);

                var previousClient = roadNetworkServiceClient;
                var previousEndpoint = previousClient?.Endpoint;
                roadNetworkServiceClient = nextClient;
                nextClient = null!;
                previousClient?.Dispose();
                if (previousEndpoint is not null && previousEndpoint != endpoint)
                    roadNetworkCredentialStore.Value.DeleteDeviceToken(previousEndpoint);

                appSettings = appSettings with
                {
                    RoadNetworkServiceEndpoint = endpoint.AbsoluteUri,
                    RoadNetworkAccountId = enrollment.AccountId,
                    RoadNetworkDisplayName = enrollment.DisplayName,
                    RoadNetworkDeviceId = enrollment.DeviceId
                };
                appSettingsStore.Save(appSettings);
                ClearRemoteRoadNetworkArchives(notifyWeb: true);
                var catalog = await roadNetworkServiceClient.GetCatalogAsync(lifetimeCancellation.Token);
                ApplyRemoteRoadNetworkCatalog(endpoint, catalog, notifyWeb: true);
                remoteRoadNetworkStatus = "connected";
            }
            finally
            {
                nextClient?.Dispose();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
                                               or IOException
                                               or TaskCanceledException
                                               or RoadNetworkServiceException
                                               or InvalidOperationException
                                               or UnauthorizedAccessException
                                               or System.ComponentModel.Win32Exception
                                               or System.Runtime.InteropServices.COMException)
        {
            remoteRoadNetworkStatus = exception switch
            {
                RoadNetworkServiceException { Code: "invalid_or_expired_enrollment_code" } => "invalidCode",
                RoadNetworkServiceException { StatusCode: HttpStatusCode.Unauthorized } => "authenticationFailed",
                System.Runtime.InteropServices.COMException
                    or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception => "credentialError",
                _ => "error"
            };
            remoteRoadNetworkError = exception is RoadNetworkServiceException serviceException
                ? serviceException.Code
                : exception.GetType().Name;
        }
        finally
        {
            remoteRoadNetworkBusy = false;
            SendSettingsState();
            remoteRoadNetworkGate.Release();
        }
    }

    private async Task DisconnectRoadNetworkServiceAsync()
    {
        try
        {
            await remoteRoadNetworkGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            remoteRoadNetworkBusy = true;
            SendSettingsState();
            if (roadNetworkServiceClient is { } client)
            {
                roadNetworkCredentialStore.Value.DeleteDeviceToken(client.Endpoint);
                client.Dispose();
                roadNetworkServiceClient = null;
            }
            else if (RoadNetworkServiceClient.TryNormalizeEndpoint(
                         appSettings.RoadNetworkServiceEndpoint, out var endpoint))
            {
                roadNetworkCredentialStore.Value.DeleteDeviceToken(endpoint);
            }

            appSettings = appSettings with
            {
                RoadNetworkAccountId = string.Empty,
                RoadNetworkDisplayName = string.Empty,
                RoadNetworkDeviceId = string.Empty
            };
            appSettingsStore.Save(appSettings);
            remoteRoadNetworkStatus = "disconnected";
            remoteRoadNetworkError = string.Empty;
            ClearRemoteRoadNetworkArchives(notifyWeb: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                               or UnauthorizedAccessException
                                               or System.ComponentModel.Win32Exception
                                               or System.Runtime.InteropServices.COMException)
        {
            remoteRoadNetworkStatus = "credentialError";
            remoteRoadNetworkError = exception.HResult.ToString("X8");
        }
        finally
        {
            remoteRoadNetworkBusy = false;
            SendSettingsState();
            remoteRoadNetworkGate.Release();
        }
    }

    private void ApplyRemoteRoadNetworkCatalog(
        Uri endpoint,
        IReadOnlyList<RoadNetworkCatalogArchive> catalog,
        bool notifyWeb)
    {
        var wasAvailable = HasRoadNetworkArchives;
        var wasEnabled = roadNetworkEnabled;
        remoteRoadNetworkArchives.Clear();
        remoteRoadNetworkArchivesByPath.Clear();
        remoteRoadNetworkCatalog.Clear();
        foreach (var archive in catalog)
        {
            if (!TryCreateRemoteRoadNetworkArchive(endpoint, archive, remoteRoadNetworkCatalog.Count, out var entry))
                continue;
            remoteRoadNetworkCatalog.Add(entry);
        }
        RebuildActiveRemoteRoadNetworkArchives();
        ReconcileRoadNetworkAvailability(wasAvailable, wasEnabled);
        if (!notifyWeb) return;
        SendRoadNetworkConfig();
        SendRoadNetworkMode();
    }

    private static bool TryCreateRemoteRoadNetworkArchive(
        Uri endpoint,
        RoadNetworkCatalogArchive archive,
        int index,
        out RemoteRoadNetworkArchive entry)
    {
        entry = null!;
        if (!RemoteArchiveIdPattern.IsMatch(archive.Id)
            || archive.Bounds is not { Length: 4 }
            || archive.Bounds.Any(value => !double.IsFinite(value))
            || archive.Bounds[0] >= archive.Bounds[2]
            || archive.Bounds[1] >= archive.Bounds[3]
            || archive.MinZoom is < 0 or > 24
            || archive.MaxZoom < archive.MinZoom
            || archive.MaxZoom > 24
            || archive.TileSize is not (256 or 512)
            || archive.Bytes <= 0
            || !Uri.TryCreate(endpoint, archive.Path, out var archiveUri)
            || !SameOrigin(endpoint, archiveUri)) return false;
        var etag = string.IsNullOrWhiteSpace(archive.Etag)
            ? $"bytes:{archive.Bytes}"
            : archive.Etag.Trim();
        entry = new RemoteRoadNetworkArchive(
            $"remote-{index}",
            archive.Id,
            archive.Name ?? new Dictionary<string, string>(),
            archiveUri.PathAndQuery,
            archive.Bounds,
            archive.MinZoom,
            archive.MaxZoom,
            archive.TileSize,
            archive.Bytes,
            etag,
            $"{endpoint.AbsoluteUri}\n{archive.Id}");
        return true;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private void ClearRemoteRoadNetworkArchives(bool notifyWeb)
    {
        var wasAvailable = HasRoadNetworkArchives;
        var wasEnabled = roadNetworkEnabled;
        remoteRoadNetworkArchives.Clear();
        remoteRoadNetworkArchivesByPath.Clear();
        remoteRoadNetworkCatalog.Clear();
        ReconcileRoadNetworkAvailability(wasAvailable, wasEnabled);
        if (!notifyWeb) return;
        SendRoadNetworkConfig();
        SendRoadNetworkMode();
    }

    private void RebuildActiveRemoteRoadNetworkArchives()
    {
        remoteRoadNetworkArchives.Clear();
        remoteRoadNetworkArchivesByPath.Clear();
        foreach (var entry in remoteRoadNetworkCatalog)
        {
            if (roadNetworkArchives.Any(local => BoundsOverlap(
                    [local.Archive.West, local.Archive.South, local.Archive.East, local.Archive.North],
                    entry.Bounds))) continue;
            remoteRoadNetworkArchives.Add(entry);
            remoteRoadNetworkArchivesByPath.Add($"/archives/{entry.RequestId}", entry);
        }
    }

    private static bool BoundsOverlap(double[] left, double[] right) =>
        left[0] < right[2] && left[2] > right[0]
        && left[1] < right[3] && left[3] > right[1];

    private async Task ClearRoadNetworkCacheAsync()
    {
        if (roadNetworkCacheBusy) return;
        roadNetworkCacheBusy = true;
        SendSettingsState();
        try
        {
            await Task.Run(() => roadNetworkRangeCache.Clear(), lifetimeCancellation.Token);
            StatusText.Text = T("Status.RoadNetworkCacheCleared");
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                               or IOException
                                               or UnauthorizedAccessException)
        {
            if (!lifetimeCancellation.IsCancellationRequested)
                StatusText.Text = T("Status.RoadNetworkCacheClearFailed");
        }
        finally
        {
            roadNetworkCacheBusy = false;
            SendSettingsState();
        }
    }

    private void OnRoadNetworkToggle(object sender, RoutedEventArgs eventArgs)
    {
        if (!HasRoadNetworkArchives) return;
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
        var available = HasRoadNetworkArchives;
        RoadNetworkButton.IsEnabled = available;
        RoadNetworkButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        RoadNetworkButton.FontWeight = roadNetworkEnabled ? FontWeights.SemiBold : FontWeights.Normal;
        RoadNetworkButton.Opacity = roadNetworkEnabled ? 1 : 0.62;
        RoadNetworkButton.ToolTip = roadNetworkEnabled
            ? TF("RoadNetwork.HideOne", RoadNetworkDescription())
            : TF("RoadNetwork.ShowOne", RoadNetworkDescription());
    }

    private string RoadNetworkDescription()
    {
        var count = roadNetworkArchives.Count + remoteRoadNetworkArchives.Count;
        if (count != 1) return TF("RoadNetwork.Count", count);
        return roadNetworkArchives.Count == 1
            ? roadNetworkArchives[0].Name
            : GetRemoteRoadNetworkDisplayName(remoteRoadNetworkArchives[0]);
    }

    private string GetRemoteRoadNetworkDisplayName(RemoteRoadNetworkArchive archive)
    {
        if (archive.Names.TryGetValue(localization.Locale, out var localized) && !string.IsNullOrWhiteSpace(localized))
            return localized;
        if (archive.Names.TryGetValue("zh-CN", out var chinese) && !string.IsNullOrWhiteSpace(chinese))
            return chinese;
        if (archive.Names.TryGetValue("en-US", out var english) && !string.IsNullOrWhiteSpace(english))
            return english;
        return archive.DatasetId;
    }

    private string GetRoadNetworkDisplayName(string path) =>
        Path.GetFileNameWithoutExtension(path) switch
        {
            "beijing-density" => T("RoadNetwork.Beijing"),
            "mentougou-density" => T("RoadNetwork.Mentougou"),
            var fileName => fileName
        };

    private void CloseRoadNetworkServices()
    {
        if (MapView.CoreWebView2 is { } core)
            core.WebResourceRequested -= OnRoadNetworkResourceRequested;
        roadNetworkServiceClient?.Dispose();
    }

    private sealed record LocalRoadNetworkArchive(string Id, string Name, PmTilesArchive Archive);
    private sealed record RoadNetworkWebArchive(
        string Id,
        string Name,
        string Url,
        int MinZoom,
        int MaxZoom,
        int TileSize,
        double[] Bounds);
    private sealed record RemoteRoadNetworkArchive(
        string RequestId,
        string DatasetId,
        IReadOnlyDictionary<string, string> Names,
        string ServicePath,
        double[] Bounds,
        int MinZoom,
        int MaxZoom,
        int TileSize,
        long Bytes,
        string ETag,
        string CacheKey);
}

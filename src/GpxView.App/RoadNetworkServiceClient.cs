using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GpxView.App;

internal sealed class RoadNetworkServiceClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private string? deviceToken;
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;

    public RoadNetworkServiceClient(Uri endpoint, string? deviceToken = null, HttpMessageHandler? handler = null)
    {
        Endpoint = endpoint;
        this.deviceToken = deviceToken;
        handler ??= new HttpClientHandler { AllowAutoRedirect = false };
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public Uri Endpoint { get; }
    public bool HasDeviceToken => !string.IsNullOrWhiteSpace(deviceToken);

    public static bool TryNormalizeEndpoint(string value, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || candidate.Scheme is not ("https" or "http")) return false;
        if (candidate.Scheme == "http" && !candidate.IsLoopback) return false;

        var builder = new UriBuilder(candidate)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        endpoint = builder.Uri;
        return true;
    }

    public async Task<RoadNetworkEnrollment> EnrollAsync(
        string code,
        string deviceName,
        string clientVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildServiceUri("v1/enroll"))
        {
            Content = JsonContent.Create(new { code, deviceName, clientVersion })
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var enrollment = await response.Content.ReadFromJsonAsync<RoadNetworkEnrollment>(JsonOptions, cancellationToken)
                         ?? throw new RoadNetworkServiceException("invalid_response");
        if (string.IsNullOrWhiteSpace(enrollment.DeviceToken)
            || string.IsNullOrWhiteSpace(enrollment.DeviceId))
            throw new RoadNetworkServiceException("invalid_response");
        deviceToken = enrollment.DeviceToken;
        accessToken = null;
        return enrollment;
    }

    public async Task<IReadOnlyList<RoadNetworkCatalogArchive>> GetCatalogAsync(
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Get, BuildServiceUri("v1/catalog"), null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var catalog = await response.Content.ReadFromJsonAsync<RoadNetworkCatalog>(JsonOptions, cancellationToken)
                      ?? throw new RoadNetworkServiceException("invalid_response");
        return catalog.Archives ?? [];
    }

    public async Task<RoadNetworkArchiveResponse> ReadArchiveAsync(
        string path,
        string method,
        string? range,
        CancellationToken cancellationToken)
    {
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = downloadCancellation.Token;
        var requestMethod = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Head
            : HttpMethod.Get;
        var uri = BuildServiceUri(path);
        using var request = new HttpRequestMessage(requestMethod, uri);
        if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);

        using var response = await SendAuthenticatedAsync(request, cancellationToken);
        if (IsRedirect(response.StatusCode)) throw new RoadNetworkServiceException("redirect_rejected");

        var contentLength = response.Content.Headers.ContentLength;
        if (requestMethod == HttpMethod.Get
            && string.IsNullOrWhiteSpace(range)
            && contentLength is > 32 * 1024 * 1024)
            throw new RoadNetworkServiceException("range_required");
        var content = requestMethod == HttpMethod.Head
            ? []
            : await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new RoadNetworkArchiveResponse(
            (int)response.StatusCode,
            response.ReasonPhrase ?? ReasonPhrase(response.StatusCode),
            content,
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.AcceptRanges.FirstOrDefault(),
            response.Content.Headers.ContentRange?.ToString(),
            contentLength,
            response.Headers.ETag?.ToString());
    }

    public string GetDeviceToken() => deviceToken ?? throw new RoadNetworkServiceException("not_enrolled");

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        return await SendAuthenticatedAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        accessToken = null;
        token = await GetAccessTokenAsync(cancellationToken);
        using var retry = await CloneAsync(request, cancellationToken);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (accessToken is { Length: > 0 } cached
            && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;
        if (string.IsNullOrWhiteSpace(deviceToken)) throw new RoadNetworkServiceException("not_enrolled");

        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is { Length: > 0 } current
                && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return current;
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildServiceUri("v1/session"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var session = await response.Content.ReadFromJsonAsync<RoadNetworkSession>(JsonOptions, cancellationToken)
                          ?? throw new RoadNetworkServiceException("invalid_response");
            if (string.IsNullOrWhiteSpace(session.AccessToken) || session.ExpiresIn < 1)
                throw new RoadNetworkServiceException("invalid_response");
            accessToken = session.AccessToken;
            accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);
            return accessToken;
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private Uri BuildServiceUri(string path)
    {
        var uri = new Uri(Endpoint, path);
        if (!string.Equals(uri.Scheme, Endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, Endpoint.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != Endpoint.Port)
            throw new RoadNetworkServiceException("cross_origin_path");
        return uri;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.SendAsync(request, completionOption, cancellationToken);
        if (IsRedirect(response.StatusCode))
        {
            response.Dispose();
            throw new RoadNetworkServiceException("redirect_rejected");
        }
        return response;
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var error = string.Empty;
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<RoadNetworkError>(JsonOptions, cancellationToken);
            error = payload?.Error ?? string.Empty;
        }
        catch (JsonException)
        {
            // The status code remains useful if an intermediary returned a non-JSON error page.
        }
        throw new RoadNetworkServiceException(
            string.IsNullOrWhiteSpace(error) ? $"http_{(int)response.StatusCode}" : error,
            response.StatusCode);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is >= 300 and < 400;

    private static string ReasonPhrase(HttpStatusCode statusCode) =>
        $"HTTP {((int)statusCode).ToString(CultureInfo.InvariantCulture)}";

    public void Dispose()
    {
        httpClient.Dispose();
        sessionGate.Dispose();
        accessToken = null;
        deviceToken = null;
    }
}

internal sealed record RoadNetworkEnrollment(
    string AccountId,
    string DisplayName,
    string DeviceId,
    string DeviceToken);

internal sealed record RoadNetworkCatalog(IReadOnlyList<RoadNetworkCatalogArchive>? Archives);

internal sealed record RoadNetworkCatalogArchive(
    string Id,
    IReadOnlyDictionary<string, string>? Name,
    string Path,
    double[] Bounds,
    int MinZoom,
    int MaxZoom,
    int TileSize,
    long Bytes,
    string Etag);

internal sealed record RoadNetworkSession(string AccessToken, int ExpiresIn);
internal sealed record RoadNetworkError(string Error);

internal sealed record RoadNetworkArchiveResponse(
    int StatusCode,
    string ReasonPhrase,
    byte[] Content,
    string? ContentType,
    string? AcceptRanges,
    string? ContentRange,
    long? ContentLength,
    string? ETag);

internal sealed class RoadNetworkServiceException(string code, HttpStatusCode? statusCode = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

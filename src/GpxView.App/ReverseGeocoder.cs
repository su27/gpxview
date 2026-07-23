using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace GpxView.App;

internal sealed class ReverseGeocoder : IDisposable
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset lastRequestUtc = DateTimeOffset.MinValue;
    private readonly HttpClient httpClient;
    private readonly string endpoint;
    private readonly string acceptLanguage;
    private readonly bool enabled;

    public ReverseGeocoder(bool enabled, string endpoint, string locale)
    {
        this.enabled = enabled && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                               && uri.Scheme is "http" or "https";
        this.endpoint = endpoint;
        acceptLanguage = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN,zh,en" : "en";
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"GpxView/{BuildInfo.Version} (+https://github.com/su27/gpxview)");
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
            locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN, zh;q=0.9, en;q=0.5" : "en");
    }

    public async Task<string?> ResolvePlaceNameAsync(double latitude, double longitude,
        CancellationToken cancellationToken)
    {
        if (!enabled || !double.IsFinite(latitude) || !double.IsFinite(longitude)) return null;

        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = TimeSpan.FromMilliseconds(1100) - (DateTimeOffset.UtcNow - lastRequestUtc);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            lastRequestUtc = DateTimeOffset.UtcNow;

            var separator = endpoint.Contains('?') ? '&' : '?';
            var requestUri = string.Create(CultureInfo.InvariantCulture,
                $"{endpoint}{separator}format=jsonv2&lat={latitude:F3}&lon={longitude:F3}&zoom=10&addressdetails=1&layer=address&accept-language={Uri.EscapeDataString(acceptLanguage)}");
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return GetPlaceName(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static string? GetPlaceName(JsonElement root)
    {
        if (root.TryGetProperty("address", out var address))
        {
            foreach (var key in new[] { "city", "municipality", "town", "county", "state_district", "village", "state" })
            {
                if (address.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
            }
        }

        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString())) return name.GetString()!.Trim();
        if (!root.TryGetProperty("display_name", out var displayName) || displayName.ValueKind != JsonValueKind.String)
            return null;
        return displayName.GetString()?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }

    public void Dispose() => httpClient.Dispose();
}

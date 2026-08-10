using GpxView.Core;

namespace GpxView.Tests;

public sealed class RoadNetworkServiceEndpointTests
{
    [Fact]
    public void ResolveMigratesLegacyWorkersDevEndpoint()
    {
        var endpoint = new Uri("https://legacy-roadnet.example.invalid/");

        var resolved = RoadNetworkServiceEndpoints.Resolve(endpoint);

        Assert.Equal(new Uri("https://roadnet.example.invalid/"), resolved);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://legacy-roadnet.example.invalid/api/")]
    public void ResolvePreservesUnrelatedEndpoints(string value)
    {
        var endpoint = new Uri(value);

        var resolved = RoadNetworkServiceEndpoints.Resolve(endpoint);

        Assert.Same(endpoint, resolved);
    }
}

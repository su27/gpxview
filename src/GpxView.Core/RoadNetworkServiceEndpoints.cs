namespace GpxView.Core;

public static class RoadNetworkServiceEndpoints
{
    private static readonly Uri Legacy = new("https://legacy-roadnet.example.invalid/");
    private static readonly Uri Current = new("https://roadnet.example.invalid/");

    public static Uri Resolve(Uri endpoint) => endpoint == Legacy ? Current : endpoint;
}

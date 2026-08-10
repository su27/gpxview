namespace GpxView.Core;

public static class RoadNetworkArchiveSelection
{
    public static string RemoteRequestId(string datasetId) => $"remote-{datasetId}";

    public static bool IsRemoteDatasetProvidedLocally(
        IEnumerable<string> localArchivePaths,
        string remoteDatasetId) => localArchivePaths.Any(path => string.Equals(
            Path.GetFileNameWithoutExtension(path),
            remoteDatasetId,
            StringComparison.OrdinalIgnoreCase));
}

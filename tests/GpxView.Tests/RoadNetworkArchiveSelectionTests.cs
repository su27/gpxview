using GpxView.Core;

namespace GpxView.Tests;

public sealed class RoadNetworkArchiveSelectionTests
{
    [Fact]
    public void RemoteRequestIdsAreStableAndDatasetSpecific()
    {
        Assert.Equal("remote-beijing-density", RoadNetworkArchiveSelection.RemoteRequestId("beijing-density"));
        Assert.Equal("remote-hebei-density", RoadNetworkArchiveSelection.RemoteRequestId("hebei-density"));
    }

    [Fact]
    public void MatchingLocalDatasetSuppressesOnlyItsRemoteCopy()
    {
        var localPaths = new[] { @"C:\RoadNetwork\beijing-density.pmtiles" };

        Assert.True(RoadNetworkArchiveSelection.IsRemoteDatasetProvidedLocally(
            localPaths, "beijing-density"));
        Assert.False(RoadNetworkArchiveSelection.IsRemoteDatasetProvidedLocally(
            localPaths, "hebei-density"));
    }

    [Fact]
    public void DatasetMatchingIsCaseInsensitive()
    {
        var localPaths = new[] { @"C:\RoadNetwork\HEBEI-DENSITY.PMTILES" };

        Assert.True(RoadNetworkArchiveSelection.IsRemoteDatasetProvidedLocally(
            localPaths, "hebei-density"));
    }
}

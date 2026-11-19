using Velopack;

namespace Composa.App.Tests;

public class DeltaUpdateTests
{
    [Fact]
    public async Task Full_download_fallback_is_refused_before_contacting_GitHub()
    {
        var source = new DeltaGithubSource(preview: true);
        var full = new VelopackAsset { Type = VelopackAssetType.Full };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.DownloadReleaseEntry(null!, full, "unused", _ => { }, TestContext.Current.CancellationToken));
    }
}

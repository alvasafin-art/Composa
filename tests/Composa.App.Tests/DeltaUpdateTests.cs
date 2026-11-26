using Velopack;

namespace Composa.App.Tests;

public class DeltaUpdateTests
{
    [Fact]
    public void Offline_retry_applies_only_to_the_requested_version_and_is_consumed_once()
    {
        var installation = Directory.CreateTempSubdirectory("composa-update-retry-");
        var retry = new UpdateRetry(installation.FullName);
        try
        {
            const string target = "1.3.0-preview.4.studio.19";
            retry.Arm(target);
            Assert.False(retry.Take("1.3.0-preview.4.studio.20"));
            Assert.True(retry.Take(target));
            Assert.False(retry.Take(target));
        }
        finally
        {
            retry.Cancel();
            installation.Delete();
        }
    }

    [Fact]
    public void A_skipped_update_clears_its_offline_retry_request()
    {
        var installation = Directory.CreateTempSubdirectory("composa-update-skip-");
        var retry = new UpdateRetry(installation.FullName);
        try
        {
            retry.Arm("1.3.0-preview.4.studio.19");
            retry.Cancel();
            Assert.Empty(installation.GetFileSystemInfos());
        }
        finally
        {
            retry.Cancel();
            installation.Delete();
        }
    }

    [Fact]
    public async Task Full_download_fallback_is_refused_before_contacting_GitHub()
    {
        var source = new DeltaGithubSource(preview: true);
        var full = new VelopackAsset { Type = VelopackAssetType.Full };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.DownloadReleaseEntry(null!, full, "unused", _ => { }, TestContext.Current.CancellationToken));
    }
}

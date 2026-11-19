using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using System.Text.Json;

namespace Composa.App;

/// <summary>The release feed supplies metadata for full packages, but only patches can cross the network.</summary>
internal sealed class DeltaGithubSource(bool preview)
    : GithubSource("https://github.com/" + AppInfo.GitHubRepository, null, preview)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // The standard source reads only ten releases, which drops intermediate patches for infrequent users.
        var result = new List<(ReleaseVersion Version, GithubRelease Release)>();
        ReleaseVersion.TryParse(AppInfo.Version, out var running);
        for (int page = 1; ; page++)
        {
            var url = $"https://api.github.com/repos/{AppInfo.GitHubRepository}/releases?per_page=100&page={page}";
            var json = await Downloader.DownloadString(url, GetRequestHeaders("application/vnd.github+json"));
            using var releases = JsonDocument.Parse(json);
            var reachedInstalled = false;
            foreach (var item in releases.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean() || !ReleaseVersion.TryParse(item.GetProperty("tag_name").GetString(), out var version)) continue;
                if (version.CompareTo(running) == 0) reachedInstalled = true;
                if (version.CompareTo(running) > 0)
                    result.Add((version, item.Deserialize<GithubRelease>()!));
            }
            if (reachedInstalled || releases.RootElement.GetArrayLength() < 100) break;
        }
        // Build one chronological patch chain. A stable client may need intermediate patches, but
        // must only be offered a stable target. Discard everything newer than that target here.
        var targets = result.Where(r => includePrereleases || (!r.Release.Prerelease && !r.Version.IsPreRelease)).ToArray();
        if (targets.Length == 0) return [];
        var latest = targets.MaxBy(r => r.Version).Version;
        return result.Where(r => r.Version.CompareTo(latest) <= 0).Select(r => r.Release).ToArray();
    }

    public override Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        if (releaseEntry.Type != VelopackAssetType.Delta)
            throw new InvalidOperationException("A verified incremental update is unavailable. Try again later or open the release notes for installation options.");
        return base.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}

internal sealed class DeltaUpdates(IUpdateSource source) : UpdateManager(source, new UpdateOptions { MaximumDeltasBeforeFallback = int.MaxValue }), IReleaseSource
{
    public static DeltaUpdates? ForInstallation()
    {
        if (!OperatingSystem.IsWindows() || UpdateCheck.Channel == UpdateChannel.Managed) return null;
        var updater = new DeltaUpdates(new DeltaGithubSource(ReleaseVersion.TryParse(AppInfo.Version, out var v) && v.IsPreRelease));
        return updater.IsInstalled ? updater : null;
    }

    public async Task<ReleaseInfo?> Latest(CancellationToken cancel)
    {
        var update = await CheckForUpdatesAsync().WaitAsync(cancel);
        var version = update?.TargetFullRelease.Version ?? UpdatePendingRestart?.Version ?? CurrentVersion!;
        return new ReleaseInfo("v" + version, AppInfo.ReleasesUrl + "/tag/v" + version, version.IsPrerelease);
    }

    public async Task<VelopackAsset> Prepare(ReleaseVersion expected, Action<int> progress, CancellationToken cancel)
    {
        if (UpdatePendingRestart is {} ready && ready.Version.ToString() == expected.ToString()) return ready;
        var update = await CheckForUpdatesAsync().WaitAsync(cancel);
        if (update?.TargetFullRelease.Version.ToString() != expected.ToString())
            throw new InvalidOperationException("The release has changed. Check for updates again.");
        if (update!.BaseRelease == null || update.DeltasToTarget.Length == 0)
            throw new InvalidOperationException("No incremental package is available for this installation. See the release notes for installation options.");
        await DownloadUpdatesAsync(update, progress, cancelToken: cancel);
        return update.TargetFullRelease;
    }

    protected override void CleanPackagesExcept(string? assetToKeep)
    {
        // Velopack also cleans up after a failed transfer. Keep the installed baseline for retries.
        if (assetToKeep != null && File.Exists(assetToKeep)) base.CleanPackagesExcept(assetToKeep);
    }
}

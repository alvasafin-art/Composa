using Composa.App.Dialogs;
namespace Composa.App;

/// <summary>
/// Checks release metadata and offers a deliberate download and restart on packaged Windows installations.
/// </summary>
public sealed partial class MainWindow
{
    private DeltaUpdates? deltaUpdates;
    private Velopack.VelopackAsset? preparedUpdate;
    private bool downloadingUpdate, restartForUpdate;
    private readonly CancellationTokenSource updateLifetime = new();
    private UpdateCheck NewUpdateCheck() => new((IReleaseSource?)deltaUpdates ?? new GitHubReleaseSource(), settings);

    /// <summary>
    /// Started once the window is up, on a background task, and never awaited. A slow or
    /// unreachable network must not hold up a single frame of startup, and an automatic check that
    /// fails says nothing at all.
    /// </summary>
    private void StartUpdateCheck()
    {
        if (!Settings.Persist) return; // Transient/headless windows do not make background network requests.
        deltaUpdates = DeltaUpdates.ForInstallation();
        Closed += (_, _) => updateLifetime.Cancel();
        var check = NewUpdateCheck();

        updateNotice.OpenReleasePage += () => UpdateNotice.OpenInBrowser(pendingUpdateUrl ?? AppInfo.ReleasesUrl);
        updateNotice.Install += () => _ = InstallUpdate();
        updateNotice.Skip += () =>
        {
            settings.SkippedVersion = pendingUpdateVersion?.ToString();
            settings.Save();
        };

        if (check.RunsAutomatically) UpdateNotice.RunInBackground(check, result =>
        {
            if (result.ShouldNotify && !updateLifetime.IsCancellationRequested) OfferUpdate(result);
        });
    }

    private ReleaseVersion? pendingUpdateVersion;
    private string? pendingUpdateUrl;

    private void OfferUpdate(UpdateResult result)
    {
        if (downloadingUpdate) return;
        pendingUpdateVersion = result.Version;
        pendingUpdateUrl = result.Url;
        preparedUpdate = deltaUpdates?.UpdatePendingRestart;
        if (preparedUpdate?.Version.ToString() != result.Version.ToString()) preparedUpdate = null;
        updateNotice.Show(result.Version, deltaUpdates != null);
        if (preparedUpdate != null) updateNotice.SetAction("Restart to update", true);
    }

    private async Task InstallUpdate()
    {
        if (deltaUpdates == null || pendingUpdateVersion == null || downloadingUpdate) return;
        if (preparedUpdate != null)
        {
            restartForUpdate = true;
            Close(); // OnClosing commits edits and handles the normal Save / Discard / Cancel flow.
            return;
        }
        downloadingUpdate = true;
        updateNotice.SetAction("Preparing…", false);
        var progress = new Progress<int>(percent =>
        {
            if (downloadingUpdate) updateNotice.SetAction($"Updating {percent}%", false);
        });
        try
        {
            preparedUpdate = await deltaUpdates.Prepare(pendingUpdateVersion.Value,
                ((IProgress<int>)progress).Report, updateLifetime.Token);
            updateNotice.SetAction("Restart to update", true);
        }
        catch (Exception ex)
        {
            updateNotice.SetAction("Retry update", true);
            if (!updateLifetime.IsCancellationRequested) await Prompts.Alert(this, "Update", ex.Message);
        }
        finally { downloadingUpdate = false; }
    }

    /// <summary>Help &gt; Check for Updates. Unlike the automatic check, this always reports what happened.</summary>
    private async Task CheckForUpdatesNow()
    {
        if (UpdateCheck.Channel == UpdateChannel.Managed)
        {
            await Prompts.Alert(this, "Check for Updates",
                $"Composa {AppInfo.Version} was installed through your package manager, which is where updates come from. " +
                "Use it to upgrade, rather than downloading a build that it does not know about.");
            return;
        }

        var result = await NewUpdateCheck().Run(manual: true);
        switch (result.Outcome)
        {
            case UpdateOutcome.Available:
                OfferUpdate(result);
                break;
            case UpdateOutcome.Failed:
                // Someone who asked deserves an answer, even when the answer is that it did not work.
                await Prompts.Alert(this, "Check for Updates",
                    "Could not reach the release page to check for a newer version. Please try again later.");
                break;
            default:
                await Prompts.Alert(this, "Check for Updates", $"Composa {AppInfo.Version} is the latest version.");
                break;
        }
    }
}

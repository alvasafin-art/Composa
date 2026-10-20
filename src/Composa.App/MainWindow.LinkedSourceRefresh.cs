using Avalonia.Threading;
using Composa.Editing;
using Composa.IO;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private sealed record SourceObservation(SmartObjectLink Revision, LinkedSourceStatus Status);
    // No bitmap/source references: keeping an observation must not keep an abandoned history alive.
    private readonly Dictionary<(EditorSession Parent, Guid Source, string Path), SourceObservation> linkedObservations = [];
    private readonly DispatcherTimer linkedSourceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Task? linkedRefresh;
    private bool linkedSourcesClosed, verifyLinkedHashes = true;

    private void InitializeLinkedSources()
    {
        layers.LinkedSourceStatus = layer => session != null && layer.SmartObject is { Link: { } link } source
            && linkedObservations.TryGetValue((session, source.Id, link.Path), out var observation) ? observation.Status : null;
        linkedSourceTimer.Tick += async (_, _) => await RefreshLinkedSources();
        Opened += (_, _) => { if (Settings.Persist) linkedSourceTimer.Start(); };
        Activated += (_, _) => verifyLinkedHashes = true;
        Closed += (_, _) => { linkedSourcesClosed = true; linkedSourceTimer.Stop(); linkedObservations.Clear(); };
    }

    internal Task RefreshLinkedSources(bool verifyHash = false)
    {
        verifyLinkedHashes |= verifyHash;
        if (linkedRefresh is { IsCompleted: false }) return linkedRefresh;
        return linkedRefresh = RefreshLinkedSourcesCore();
    }

    private bool CanRefreshSource(EditorSession parent, SmartObjectSource source)
    {
        if (linkedSourcesClosed || !sessions.Contains(parent) || parent.IsInteracting || canvas.IsDragging || OwnedWindows.Any()
            || saving.ContainsKey(parent) || source.Link is not { HasLocalEdits: false }) return false;
        if (!parent.Document.AllLayers().Any(l => ReferenceEquals(l.SmartObject, source))) return false;
        return !HasUnsavedSmartContents(parent, source);
    }

    private bool HasUnsavedSmartContents(EditorSession parent, SmartObjectSource source) => embeddedTabs.Any(pair => pair.Value.Parent == parent
        && pair.Value.Source.Id == source.Id && (pair.Key.IsModified || pair.Key.IsInteracting || saving.ContainsKey(pair.Key)
            || embeddedTabs.Values.Any(child => child.Parent == pair.Key)));

    private void RefreshSmartContentsTabs(EditorSession parent, Guid sourceId)
    {
        var replacement = parent.Document.AllLayers().First(l => l.SmartObject?.Id == sourceId).SmartObject!;
        foreach (var (contents, tab) in embeddedTabs.Where(pair => pair.Value.Parent == parent && pair.Value.Source.Id == sourceId).ToArray())
        {
            if (!ReferenceEquals(tab.Source.Preview, replacement.Preview)) contents.ReloadEmbeddedContents(replacement.OpenDocument());
            embeddedTabs[contents] = tab with { Source = replacement };
        }
    }

    private async Task RefreshLinkedSourcesCore()
    {
        if (!settings.AutoUpdateLinkedSources || linkedSourcesClosed || canvas.IsDragging || OwnedWindows.Any()) return;
        var verify = verifyLinkedHashes; verifyLinkedHashes = false;
        foreach (var key in linkedObservations.Keys.Where(key => !sessions.Contains(key.Parent)).ToArray()) linkedObservations.Remove(key);
        // Keep recent paths across Undo/Relink, but bound metadata growth in very long sessions.
        while (linkedObservations.Count > 1024) linkedObservations.Remove(linkedObservations.Keys.First());
        var sources = sessions.SelectMany(parent => parent.Document.AllLayers().Select(l => l.SmartObject)
            .OfType<SmartObjectSource>().DistinctBy(s => s.Id).Select(source => (parent, source))).ToArray();
        var changedStatus = false;
        foreach (var (parent, source) in sources)
        {
            if (!CanRefreshSource(parent, source)) continue;
            var link = source.Link!; var key = (parent, source.Id, link.Path);
            linkedObservations.TryGetValue(key, out var previous);
            SmartObjectLink? probedRevision = null;
            try
            {
                CheckSmartObjectPath(parent, link.Path);
                var observed = await Task.Run(() => LinkedSmartObjectFile.Probe(previous?.Revision ?? link, verify));
                if (!CanRefreshSource(parent, source)) continue;
                var revision = observed.Revision ?? previous?.Revision ?? link;
                probedRevision = observed.Revision;
                if (observed.State == LinkedSourceState.Modified && revision.Sha256 == link.Sha256)
                    observed = new(LinkedSourceState.Current);
                else if (observed.State == LinkedSourceState.Modified)
                {
                    using var loaded = await Task.Run(() => LinkedSmartObjectFile.Load(link.Path, mergedAppearance: link.UseMergedAppearance));
                    if (!CanRefreshSource(parent, source)) continue;
                    revision = loaded.Link;
                    if (!link.UseMergedAppearance && loaded.Conversions.Count > 0 && loaded.Link.ConversionSignature != link.ConversionSignature)
                        observed = new(LinkedSourceState.Modified, "The changed Photoshop source needs conversion review. Choose Update from File to review it.");
                    else
                    {
                        parent.ReplaceSmartObject(source, loaded.Document, loaded.Link, "Update Linked Smart Object"); loaded.Adopt();
                        RefreshSmartContentsTabs(parent, source.Id);
                        observed = new(LinkedSourceState.Current);
                    }
                }
                else if (observed.State == LinkedSourceState.Current && revision.Sha256 != link.Sha256)
                {
                    // The same on-disk revision was already adopted/reviewed. Undo deliberately restores the cache.
                    // Do not turn polling into an automatic Redo.
                    observed = previous?.Status.State is LinkedSourceState.Modified or LinkedSourceState.Unavailable ? previous.Status
                        : new(LinkedSourceState.Modified, "Cached contents differ from the last observed file revision. Choose Update from File to adopt it again.");
                }
                var next = new SourceObservation(revision, observed with { Revision = null });
                if (next != previous) { linkedObservations[key] = next; changedStatus = true; }
            }
            catch (Exception error)
            {
                if (!CanRefreshSource(parent, source)) continue;
                var revision = error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException ? probedRevision : null;
                var next = new SourceObservation(revision ?? previous?.Revision ?? link, new(LinkedSourceState.Unavailable, error.Message));
                if (next != previous) { linkedObservations[key] = next; changedStatus = true; }
            }
        }
        if (changedStatus && !linkedSourcesClosed) layers.RefreshSourceStatus();
    }
}

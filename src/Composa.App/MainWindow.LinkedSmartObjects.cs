using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task<LinkedSmartObjectFile?> ReadSmartObjectFile(string path, bool review, bool mergedAppearance = false)
    {
        mergedAppearance &= Composa.IO.Psd.PsdImport.IsPsd(path);
        var loaded = await Task.Run(() => LinkedSmartObjectFile.Load(path, mergedAppearance: mergedAppearance));
        try
        {
            if (loaded.Conversions.Count > 0 && !mergedAppearance)
            {
                if (!review) throw new InvalidOperationException("This Photoshop source needs conversion review from the File menu.");
                var merged = false;
                if (!await PsdConversionDialog.Confirm(this, Path.GetFileName(path), loaded.Conversions, chooseMerged: value => merged = value)) { loaded.Dispose(); return null; }
                if (merged)
                {
                    loaded.Dispose(); loaded = await Task.Run(() => LinkedSmartObjectFile.Load(path, mergedAppearance: true));
                }
            }
            return loaded;
        }
        catch { loaded.Dispose(); throw; }
    }

    public async Task<Layer?> PlaceSmartObjectPath(string path, bool linked = true, EditorSession? target = null, bool review = true)
    {
        var parent = target ?? session ?? throw new InvalidOperationException("Open a document first.");
        if (linked) CheckSmartObjectPath(parent, path);
        if (canvas.IsDragging || parent.IsInteracting && !parent.IsEditingText) throw new InvalidOperationException("Finish the current edit before placing a source.");
        parent.FinishText();
        using var loaded = await Busy(() => ReadSmartObjectFile(path, review)); if (loaded == null) return null;
        if (!sessions.Contains(parent) || parent.IsInteracting) throw new InvalidOperationException("The target document changed while the source was loading. Try again.");
        var layer = parent.PlaceSmartObject(loaded.Document, Path.GetFileNameWithoutExtension(path), linked ? loaded.Link : null); loaded.Adopt();
        return layer;
    }

    public async Task<bool> ReplaceSmartObjectPath(Layer layer, string path, bool relink = false, EditorSession? target = null, bool review = true)
    {
        var parent = target ?? session ?? throw new InvalidOperationException("Open a document first.");
        if (parent.Document.Find(layer.Id) != layer || layer.SmartObject is not { } expected) throw new InvalidOperationException("Select a smart object first.");
        if (HasUnsavedSmartContents(parent, expected)) throw new InvalidOperationException("Save or close this smart object's open contents before replacing its source. Your edits are kept in their tab.");
        if (relink || expected.Link != null) CheckSmartObjectPath(parent, path);
        if (canvas.IsDragging || parent.IsInteracting && !parent.IsEditingText) throw new InvalidOperationException("Finish the current edit before replacing contents.");
        parent.FinishText();
        using var loaded = await Busy(() => ReadSmartObjectFile(path, review, relink && expected.Link?.UseMergedAppearance == true)); if (loaded == null) return false;
        if (!sessions.Contains(parent) || parent.IsInteracting) throw new InvalidOperationException("The target document changed while the source was loading. Try again.");
        if (HasUnsavedSmartContents(parent, expected)) throw new InvalidOperationException("The open contents changed while the source was loading. Save or close them first; your edits are kept.");
        parent.ReplaceSmartObject(expected, loaded.Document, relink || expected.Link != null ? loaded.Link : null,
            relink ? "Relink Smart Object" : "Replace Smart Object Contents"); loaded.Adopt(); RefreshSmartContentsTabs(parent, expected.Id); return true;
    }

    public async Task<bool> UpdateLinkedSmartObject(EditorSession parent, SmartObjectSource expected, bool review = true, bool discardLocal = false)
    {
        if (expected.Link is not { } link) throw new InvalidOperationException("This smart object is embedded.");
        if (HasUnsavedSmartContents(parent, expected)) throw new InvalidOperationException("Save or close this smart object's open contents before updating from its source. Your edits are kept in their tab.");
        if (canvas.IsDragging || parent.IsInteracting) throw new InvalidOperationException("Finish the current edit before updating the source.");
        CheckSmartObjectPath(parent, link.Path);
        using var loaded = await Busy(() => ReadSmartObjectFile(link.Path, review, link.UseMergedAppearance)); if (loaded == null) return false;
        if (link.HasLocalEdits && !discardLocal)
        {
            if (!review) throw new InvalidOperationException("The cached source has local edits. Save them or explicitly choose to discard them before updating.");
            if (!await Prompts.Confirm(this, "Update linked source?", "Replace the locally edited cached contents with this file's current contents?", "Update from File")) return false;
        }
        if (!sessions.Contains(parent) || parent.IsInteracting) throw new InvalidOperationException("The target document changed while the source was loading. Try again.");
        if (HasUnsavedSmartContents(parent, expected)) throw new InvalidOperationException("The open contents changed while the source was loading. Save or close them first; your edits are kept.");
        if (link.Sha256 == loaded.Link.Sha256 && !link.HasLocalEdits && link.UseMergedAppearance == loaded.Link.UseMergedAppearance)
        {
            parent.SetSmartObjectLink(expected, loaded.Link); RefreshSmartContentsTabs(parent, expected.Id); return false;
        }
        parent.ReplaceSmartObject(expected, loaded.Document, loaded.Link, "Update Linked Smart Object"); loaded.Adopt(); RefreshSmartContentsTabs(parent, expected.Id); return true;
    }

    private static void CheckSmartObjectPath(EditorSession parent, string path)
    {
        if (parent.FilePath is { } own && string.Equals(Path.GetFullPath(own), Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("A document cannot link to its own file.");
    }

    private async Task ChooseSmartObjectFile(bool linked, Layer? replacement = null, bool relink = false)
    {
        var parent = session; if (parent == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = replacement == null ? linked ? "Place Linked Smart Object" : "Place Embedded Smart Object" : relink ? "Relink Smart Object" : "Replace Smart Object Contents",
            AllowMultiple = false, FileTypeFilter = [AnyOpenable, ProjectType, ImageType] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            if (replacement == null) await PlaceSmartObjectPath(path, linked, parent);
            else await ReplaceSmartObjectPath(replacement, path, relink, parent);
        }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task UpdateLinkedFromUi(Layer layer)
    {
        if (session is not { } parent || layer.SmartObject is not { Link: not null } source) return;
        try { await UpdateLinkedSmartObject(parent, source); }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task LinkedSourceInfo(Layer layer)
    {
        if (layer.SmartObject is not { Link: { } link } source) return;
        var parent = session; if (parent == null) return;
        var state = await Task.Run(() => LinkedSmartObjectFile.Probe(link, verifyHash: true));
        if (!sessions.Contains(parent) || parent.Document.Find(layer.Id)?.SmartObject != source) return;
        if (state.State == LinkedSourceState.Modified && linkedObservations.TryGetValue((parent, source.Id, link.Path), out var observation)
            && observation.Revision.Sha256 == state.Revision?.Sha256 && observation.Status.State == LinkedSourceState.Modified) state = observation.Status;
        var path = Ui.Label(link.Path, Palette.Secondary); path.MaxWidth = 440; path.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var fileName = Ui.Label(Path.GetFileName(link.Path.Replace('\\', '/'))); fileName.MaxWidth = 440; fileName.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; ToolTip.SetTip(fileName, fileName.Text);
        var body = Ui.Column(12, fileName, path,
            Ui.Label(state.State switch { LinkedSourceState.Current => "Source is up to date", LinkedSourceState.Modified => "Source file has changed", LinkedSourceState.Missing => "Source file is missing · cached contents remain available",
                LinkedSourceState.LocalEdits => "Cached contents have local edits", _ => "Source file is unavailable" }, Palette.Secondary));
        if (state.Detail != null) { var detail = Ui.Label(state.Detail, Palette.Secondary); detail.MaxWidth = 440; detail.TextWrapping = Avalonia.Media.TextWrapping.Wrap; body.Children.Add(detail); }
        var dialog = new DialogWindow("Linked Smart Object", body, "Close", cancellable: false);
        var update = Ui.TextButton("Update from File", async () => { dialog.Close(true); await UpdateLinkedFromUi(layer); });
        update.IsEnabled = state.State is not (LinkedSourceState.Missing or LinkedSourceState.Unavailable);
        var automatic = new CheckBox { Content = "Automatically update changed linked files", IsChecked = settings.AutoUpdateLinkedSources };
        automatic.IsCheckedChanged += (_, _) => { settings.AutoUpdateLinkedSources = automatic.IsChecked == true; settings.Save(); };
        body.Children.Add(automatic);
        var buttons = Ui.Row(8, update,
            Ui.TextButton("Relink…", async () => { dialog.Close(true); await ChooseSmartObjectFile(true, layer, relink: true); }),
            Ui.TextButton("Embed", () => { dialog.Close(true); parent.SetSmartObjectLink(source, null); }));
        body.Children.Add(buttons);
        body.Children.Add(Ui.TextButton("Save editable copy and relink…", async () =>
        {
            dialog.Close(true);
            try { SetSession(parent); var contents = OpenSmartObject(layer); await SaveSmartObjectContents(contents, editableCopy: true); }
            catch (Exception error) { ShowProblem(error.Message); }
        }));
        await dialog.Ask(this);
    }
}

using Composa.Editing;
using Composa.Model;
using Composa.IO;
using Composa.App.Dialogs;
using Avalonia.Platform.Storage;

namespace Composa.App;

public sealed partial class MainWindow
{
    private sealed record EmbeddedTab(EditorSession Parent, SmartObjectSource Source);
    private readonly Dictionary<EditorSession, EmbeddedTab> embeddedTabs = [];

    public EditorSession OpenSmartObject(Layer layer)
    {
        if (session is not { } parent || parent.Document.Find(layer.Id) != layer || layer.SmartObject is not { } source)
            throw new InvalidOperationException("Select a smart object first.");
        if (embeddedTabs.FirstOrDefault(pair => pair.Value.Parent == parent && pair.Value.Source.Id == source.Id).Key is { } open)
        {
            if (!ReferenceEquals(embeddedTabs[open].Source, source))
            {
                if (ReferenceEquals(embeddedTabs[open].Source.Preview, source.Preview)) embeddedTabs[open] = new(parent, source);
                else if (!open.IsModified && !open.IsEditingText && !embeddedTabs.Values.Any(tab => tab.Parent == open))
                {
                    open.ReloadEmbeddedContents(source.OpenDocument());
                    embeddedTabs[open] = new(parent, source);
                }
                else ShowProblem("The parent smart object changed. Your existing contents are kept; save them as a separate project or close their tabs and reopen to use the parent's version.");
            }
            SetSession(open); return open;
        }
        var contents = new EditorSession(source.OpenDocument()) { SuggestedName = layer.Name + " · Contents" };
        contents.History.BaseName = "Smart Object Contents";
        embeddedTabs.Add(contents, new(parent, source));
        AddSession(contents); return contents;
    }

    internal async Task<bool> SaveSmartObjectContents(EditorSession contents, bool editableCopy = false, string? copyPath = null, bool overwrite = false)
    {
        if (saving.TryGetValue(contents, out var previous)) await previous.Task;
        if (!embeddedTabs.TryGetValue(contents, out var tab)) return false;
        if (!sessions.Contains(tab.Parent)) throw new InvalidOperationException("The parent document is closed. Save these contents as a separate project instead.");
        var currentSource = tab.Parent.Document.AllLayers().FirstOrDefault(l => l.SmartObject?.Id == tab.Source.Id)?.SmartObject;
        if (currentSource != null && !ReferenceEquals(currentSource, tab.Source) && ReferenceEquals(currentSource.Preview, tab.Source.Preview))
            embeddedTabs[contents] = tab = tab with { Source = currentSource };
        if (canvas.IsDragging || contents.IsInteracting && !contents.IsEditingText) throw new InvalidOperationException("Finish the current edit before saving contents.");
        if (contents.IsEditingText) contents.FinishText();
        if (!editableCopy && !contents.IsModified && tab.Source.Link?.HasLocalEdits != true) return true;
        if (tab.Source.Link is { } link)
        {
            var snapshot = contents.Document.Clone(); var state = contents.History.CurrentId;
            var task = ReviewAndWriteLinkedContents(contents, tab, snapshot, state, link, editableCopy, copyPath, overwrite);
            saving[contents] = (link.Path, task); UpdateStatus();
            try { var error = await task; if (error is OperationCanceledException) return false; if (error != null) throw error; return true; }
            finally { if (saving.TryGetValue(contents, out var current) && current.Task == task) saving.Remove(contents); UpdateStatus(); }
        }
        tab.Parent.UpdateSmartObject(tab.Source, contents.Document);
        var replacement = tab.Parent.Document.AllLayers().First(layer => layer.SmartObject?.Id == tab.Source.Id).SmartObject!;
        embeddedTabs[contents] = tab with { Source = replacement };
        contents.MarkEmbeddedSaved(); return true;
    }

    private async Task<Exception?> ReviewAndWriteLinkedContents(EditorSession contents, EmbeddedTab tab, Document snapshot, long state, SmartObjectLink link,
        bool editableCopy, string? copyPath, bool overwrite)
    {
        try
        {
            var destination = link.Path; SmartObjectLink? expected = link;
            if (editableCopy || link.UseMergedAppearance || !LinkedSmartObjectFile.CanSave(link.Path))
            {
                if (copyPath == null)
                {
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    { Title = "Save Editable Copy and Relink", SuggestedFileName = Path.GetFileNameWithoutExtension(link.Path) + ProjectFile.Extension,
                        DefaultExtension = ProjectFile.Extension.TrimStart('.'), FileTypeChoices = [ProjectType], ShowOverwritePrompt = true });
                    copyPath = file?.TryGetLocalPath(); if (copyPath == null) return new OperationCanceledException();
                    if (!Path.HasExtension(copyPath)) copyPath += ProjectFile.Extension;
                    overwrite = true; // The native save picker reviewed this destination.
                }
                destination = Path.GetFullPath(copyPath);
                if (!Path.GetExtension(destination).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An editable source copy ends in .cmps.");
                if (string.Equals(destination, Path.GetFullPath(link.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Choose a different path for the editable source copy.");
                CheckSmartObjectPath(tab.Parent, destination); expected = null;
            }
            var conversions = await Task.Run(() => LinkedSmartObjectFile.SaveConversions(snapshot, destination));
            if (conversions.Count > 0 && !await Prompts.Confirm(this, "Save linked source?", string.Join("\n\n", conversions.Select(c => c.Message).Distinct()), "Save Source")) return new OperationCanceledException();
            var instances = tab.Parent.Document.AllLayers().Where(l => l.SmartObject?.Id == tab.Source.Id).ToArray();
            if (!sessions.Contains(contents) || !sessions.Contains(tab.Parent) || instances.Length == 0 || instances.Any(l => !ReferenceEquals(l.SmartObject, tab.Source)))
                throw new InvalidOperationException("The parent source changed. Your contents remain available; reopen the parent source or save an editable copy.");
            return await WriteLinkedContents(contents, tab, snapshot, state, destination, expected, overwrite);
        }
        catch (Exception error) { return error; }
    }

    private async Task<Exception?> WriteLinkedContents(EditorSession contents, EmbeddedTab tab, Document snapshot, long state, string destination, SmartObjectLink? expected, bool overwrite)
    {
        var written = false;
        try
        {
            using var loaded = await Task.Run(() =>
            {
                var revision = LinkedSmartObjectFile.Save(snapshot, destination, expected, overwrite: overwrite, allowConversion: true);
                written = true;
                var result = LinkedSmartObjectFile.Load(destination);
                if (result.Link.Sha256 != revision.Sha256) { result.Dispose(); throw new IOException("The source changed again immediately after saving. Your editing workspace remains available."); }
                return result;
            });
            if (!sessions.Contains(tab.Parent)) throw new InvalidOperationException("The parent document is closed.");
            tab.Parent.ReplaceSmartObject(tab.Source, loaded.Document, loaded.Link, "Save Linked Smart Object"); loaded.Adopt();
            var replacement = tab.Parent.Document.AllLayers().First(layer => layer.SmartObject?.Id == tab.Source.Id).SmartObject!;
            embeddedTabs[contents] = tab with { Source = replacement };
            contents.MarkEmbeddedSaved(state); if (!contents.IsModified) recovery?.Forget(contents); return null;
        }
        catch (Exception error)
        {
            return written ? new IOException("The source file was saved, but its parent could not be refreshed. Your editing workspace is kept. Update or reopen the linked source. " + error.Message, error) : error;
        }
    }
}

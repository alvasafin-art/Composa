using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly ElementLibrary elementLibrary;
    private readonly ResourceLibraryPanel libraryPanel;
    private bool capturingLibraryElement;

    private ResourceLibraryPanel BuildResourceLibrary()
    {
        var panel = new ResourceLibraryPanel(elementLibrary);
        layers.CanDragLibrarySelection = () => !settings.CanvasOnly && panel.IsEffectivelyVisible && panel.CanSave;
        panel.AcceptLayerDrop = data => layers.IsLibrarySelectionDrag(data, session);
        layers.SaveLibraryElementRequested += () => _ = SaveLibrarySelection();
        panel.SaveRequested += () => _ = SaveLibrarySelection(fromDrop: true);
        panel.ReplaceRequested += entry => _ = SaveLibrarySelection(entry);
        panel.InsertRequested += entry => _ = InsertLibraryEntry(entry);
        panel.RenameRequested += entry => _ = RenameLibraryEntry(entry);
        panel.DeleteRequested += entry => _ = DeleteLibraryEntry(entry);
        panel.FolderRequested += () => _ = OpenLibraryFolder();
        panel.Problem += ShowProblem;

        DragDrop.SetAllowDrop(canvas, true);
        canvas.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (panel.DraggedEntry(e.DataTransfer) == null) return;
            e.DragEffects = session == null || IsDragging ? DragDropEffects.None : DragDropEffects.Copy;
            canvas.LibraryDropPoint = e.DragEffects == DragDropEffects.Copy ? canvas.ToDocument(e.GetPosition(canvas)) : null;
            e.Handled = true;
        });
        canvas.AddHandler(DragDrop.DragLeaveEvent, (_, _) => canvas.LibraryDropPoint = null);
        canvas.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            canvas.LibraryDropPoint = null;
            if (panel.DraggedEntry(e.DataTransfer) is not { } entry) return;
            e.Handled = true;
            _ = InsertLibraryEntry(entry, canvas.ToDocument(e.GetPosition(canvas)));
        });
        return panel;
    }

    private void RefreshLibraryActions() =>
        libraryPanel.CanSave = !capturingLibraryElement && session is { CanSaveLibraryElement: true, IsInTransaction: false };

    private async Task SaveLibrarySelection(LibraryEntry? replace = null, bool fromDrop = false)
    {
        if (session is not { CanSaveLibraryElement: true, IsInTransaction: false } target || IsDragging || capturingLibraryElement) return;
        capturingLibraryElement = true;
        RefreshLibraryActions();
        try
        {
            target.CommitChannelControlEdit();
            if (!CommitWarp(target)) return;
            var snapshot = target.CaptureLibraryElement(out var embedded);
            var (thumbnail, fonts) = await Task.Run(() => (ElementLibrary.RenderThumbnail(snapshot), ElementLibrary.FontDependencies(snapshot)));
            if (session != target || !sessions.Contains(target)) return;
            using var stream = new MemoryStream(thumbnail, writable: false);
            using var preview = new Bitmap(stream);
            var missing = ResourceLibraryPanel.MissingFonts(fonts);
            var name = fromDrop ? await Task.Run(() => NextResourceName(snapshot.Layers[0].Name)) :
                await LibraryNameDialog(replace?.Name ?? snapshot.Layers[0].Name,
                replace == null ? "Save to Library" : "Update Library Resource", replace == null ? "Save" : "Update",
                preview, missing, embedded, rename: replace == null);
            if (name == null || !sessions.Contains(target)) return;
            await WaitForSave(target);
            if (!sessions.Contains(target)) return;

            LibraryEntry? saved = null;
            var error = await TrackSave(target, name, Task.Run<Exception?>(() =>
            {
                try { saved = elementLibrary.Save(name, snapshot, thumbnail, embedded, replace); return null; }
                catch (Exception failure) { return failure; }
            }));
            if (error != null) throw error;
            await libraryPanel.Refresh(saved!.Id);
            problem = null;
            ShowNote(replace == null ? $"Saved to library: {name}" : $"Updated library resource: {name}");
        }
        catch (Exception error) { await LibraryProblem(error); }
        finally
        {
            capturingLibraryElement = false;
            RefreshLibraryActions();
        }
    }

    private string NextResourceName(string source)
    {
        var proposed = ElementLibrary.NormalizeName(source[..Math.Min(source.Length, ElementLibrary.MaxNameLength)]) ?? Locale.T("Resource");
        var used = elementLibrary.List().Entries.Select(entry => entry.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = proposed;
        for (int number = 2; used.Contains(name); number++)
        {
            var suffix = $" {number}";
            name = proposed[..Math.Min(proposed.Length, ElementLibrary.MaxNameLength - suffix.Length)].TrimEnd() + suffix;
        }
        return name;
    }

    private async Task InsertLibraryEntry(LibraryEntry entry, SKPoint? center = null)
    {
        if (session is not { IsInTransaction: false } target || IsDragging) return;
        try
        {
            var profile = target.Document.Profile;
            var element = await Task.Run(() => EditorSession.PrepareLibraryElement(elementLibrary.Load(entry), profile));
            if (session != target || !sessions.Contains(target) || IsDragging || target.IsInTransaction) return;
            if (!target.Document.Profile.Equals(profile))
            {
                ShowProblem("The document color profile changed. Insert the resource again.");
                return;
            }
            target.CommitChannelControlEdit();
            if (!CommitWarp(target)) return;
            target.InsertLibraryElement(element, center);
            var missing = ResourceLibraryPanel.MissingFonts(entry.Fonts);
            if (missing.Length > 0) ShowProblem($"Missing fonts: {string.Join(", ", missing)}");
            else
            {
                problem = null;
                ShowNote($"Inserted library resource: {entry.Name}");
            }
        }
        catch (Exception error) { await LibraryProblem(error); }
    }

    private async Task RenameLibraryEntry(LibraryEntry entry)
    {
        try
        {
            var name = await LibraryNameDialog(entry.Name, "Rename Library Resource", "Rename");
            if (name == null || name == entry.Name) return;
            var renamed = await Task.Run(() => elementLibrary.Rename(entry, name));
            await libraryPanel.Refresh(renamed.Id);
        }
        catch (Exception error) { await LibraryProblem(error); }
    }

    private async Task DeleteLibraryEntry(LibraryEntry entry)
    {
        try
        {
            if (!await Prompts.Confirm(this, "Delete Library Resource", $"Delete “{entry.Name}” from the library?", "Delete")) return;
            await Task.Run(() => elementLibrary.Delete(entry));
            await libraryPanel.Refresh();
        }
        catch (Exception error) { await LibraryProblem(error); }
    }

    private async Task<string?> LibraryNameDialog(string name, string title, string action, Bitmap? thumbnail = null,
        string[]? missingFonts = null, bool embedded = false, bool rename = true)
    {
        var body = Ui.Column(8);
        body.Width = 300;
        if (thumbnail != null)
            body.Children.Add(new Border
            {
                Height = 152, Background = ResourceLibraryPanel.ThumbnailMatte, CornerRadius = new CornerRadius(5),
                Child = new Image { Source = thumbnail, Stretch = Stretch.Uniform }
            });
        var field = new TextBox
        {
            Name = "LibraryResourceName", MaxLength = ElementLibrary.MaxNameLength, MinHeight = 30,
            Text = name.Length > ElementLibrary.MaxNameLength ? name[..ElementLibrary.MaxNameLength] : name
        };
        if (rename) body.Children.Add(CanvasLayoutEditor.Field("Name", field));
        else body.Children.Add(Ui.Label(name, weight: FontWeight.SemiBold));
        void Warn(string message) => body.Children.Add(new TextBlock
            { Text = Locale.Message(message), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (missingFonts is { Length: > 0 }) Warn($"Missing fonts: {string.Join(", ", missingFonts)}");
        if (embedded) Warn("Linked smart objects will be saved as embedded copies of their cached contents.");
        var dialog = new DialogWindow(title, body, action);
        if (rename)
        {
            dialog.CanAccept = ElementLibrary.NormalizeName(field.Text) != null;
            field.TextChanged += (_, _) => dialog.CanAccept = ElementLibrary.NormalizeName(field.Text) != null;
            dialog.Opened += (_, _) => { field.Focus(); field.SelectAll(); };
        }
        return await dialog.Ask(this) ? rename ? ElementLibrary.NormalizeName(field.Text) : name : null;
    }

    private async Task OpenLibraryFolder()
    {
        try
        {
            Directory.CreateDirectory(elementLibrary.Folder);
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(elementLibrary.Folder)))
                ShowProblem($"Couldn't open the folder {elementLibrary.Folder}.");
        }
        catch (Exception error) { await LibraryProblem(error); }
    }

    private async Task LibraryProblem(Exception error)
    {
        ShowProblem($"Library: {error.Message}");
        await libraryPanel.Refresh();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class LinkedSmartObjectUiTests
{
    [AvaloniaFact]
    public async Task Changed_photoshop_source_requires_review_only_when_its_conversion_report_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-psd-" + Guid.NewGuid().ToString("N") + ".psd");
        var window = new MainWindow { Width = 1000, Height = 740 }; window.Show();
        using var pixels = Pixels.NewColor(80, 60);
        void Write(SKColor color, bool unsupported)
        {
            pixels.Erase(color); var writer = new Composa.Core.Tests.PsdWriter { Width = 80, Height = 60, Composite = pixels };
            var layer = new Composa.Core.Tests.PsdWriterLayer { Name = "Source", Image = pixels };
            if (unsupported) layer.With("lfx2", new byte[16]); writer.Layers.Add(layer); File.WriteAllBytes(path, writer.Build());
        }
        try
        {
            Write(SKColors.Blue, false); var parent = EditorSession.NewCanvas(240, 180); window.AddSession(parent);
            var layer = (await window.PlaceSmartObjectPath(path, review: false))!; await window.RefreshLinkedSources();
            Write(SKColors.Red, true); await window.RefreshLinkedSources();
            Assert.Empty(window.OwnedWindows); Assert.Equal(SKColors.Blue, layer.Pixels!.GetPixel(20, 20));
            await Assert.ThrowsAsync<InvalidOperationException>(() => window.UpdateLinkedSmartObject(parent, layer.SmartObject!, review: false));
            var updating = window.UpdateLinkedSmartObject(parent, layer.SmartObject!);
            for (var i = 0; i < 200 && !window.OwnedWindows.Any(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var dialog = Assert.Single(window.OwnedWindows); dialog.Close(true); Assert.True(await updating);
            Assert.NotNull(layer.SmartObject!.Link!.ConversionSignature); Assert.Equal(SKColors.Red, layer.Pixels.GetPixel(20, 20));
            await window.RefreshLinkedSources(); Write(SKColors.Yellow, true); await window.RefreshLinkedSources();
            Assert.Empty(window.OwnedWindows); Assert.Equal(SKColors.Yellow, layer.Pixels.GetPixel(20, 20));
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Editable_source_copy_relinks_all_instances_without_overwriting_original_or_existing_destination()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-copy-" + Guid.NewGuid().ToString("N") + ".png");
        var copy = path + ".cmps"; var window = new MainWindow { Width = 1000, Height = 740 }; window.Show();
        try
        {
            using var image = Pixels.NewColor(80, 60); image.Erase(SKColors.Blue); ImageFiles.Save(image, path, ExportFormat.Png);
            var original = File.ReadAllBytes(path); var parent = EditorSession.NewCanvas(240, 180); window.AddSession(parent);
            var layer = (await window.PlaceSmartObjectPath(path))!; parent.DuplicateSelectedLayers(); var twin = parent.ActiveLayer!;
            var contents = window.OpenSmartObject(layer); contents.Fill(SKColors.Orange);
            ProjectFile.Save(EditorSession.NewCanvas(10, 10, SKColors.Green).Document, copy);
            await Assert.ThrowsAsync<IOException>(() => window.SaveSmartObjectContents(contents, editableCopy: true, copyPath: copy));
            Assert.Equal(10, ProjectFile.Load(copy).Width); Assert.True(contents.IsModified); Assert.Equal(path, layer.SmartObject!.Link!.Path);
            Assert.True(await window.SaveSmartObjectContents(contents, editableCopy: true, copyPath: copy, overwrite: true));
            Assert.False(contents.IsModified); Assert.Equal(copy, layer.SmartObject.Link!.Path); Assert.Same(layer.SmartObject, twin.SmartObject);
            Assert.Equal(SKColors.Orange, layer.Pixels!.GetPixel(20, 20)); Assert.Equal(original, File.ReadAllBytes(path));
            var state = parent.History.CurrentId; await window.RefreshLinkedSources(verifyHash: true); Assert.Equal(state, parent.History.CurrentId);
            contents.Fill(SKColors.Purple); Assert.True(await window.SaveSmartObjectContents(contents));
            Assert.Equal(SKColors.Purple, ProjectFile.Load(copy).ActiveLayer!.Pixels!.GetPixel(20, 20)); Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { window.Close(); File.Delete(path); File.Delete(copy); }
    }

    [AvaloniaFact]
    public async Task Automatic_refresh_updates_shared_instances_and_clean_contents_but_respects_undo_and_disabled_updates()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-auto-" + Guid.NewGuid().ToString("N") + ".cmps");
        var window = new MainWindow { Width = 1000, Height = 740 }; window.Show();
        try
        {
            void Write(SKColor color) => ProjectFile.Save(EditorSession.NewCanvas(80, 60, color).Document, path);
            Write(SKColors.Blue);
            var parent = EditorSession.NewCanvas(240, 180); window.AddSession(parent);
            var layer = (await window.PlaceSmartObjectPath(path))!; var id = layer.Id; var frame = layer.Transform;
            parent.DuplicateSelectedLayers(); var copyId = parent.ActiveLayer!.Id;
            var contents = window.OpenSmartObject(layer); await window.RefreshLinkedSources();
            Write(SKColors.Red); await window.RefreshLinkedSources();
            layer = parent.Document.Find(id)!;
            Assert.Equal(SKColors.Red, layer.Pixels!.GetPixel(20, 20)); Assert.Equal(frame, layer.Transform);
            Assert.Same(layer.SmartObject, parent.Document.Find(copyId)!.SmartObject);
            Assert.Equal(SKColors.Red, contents.ActiveLayer!.Pixels!.GetPixel(20, 20)); Assert.False(contents.IsModified);
            parent.Undo(); var undone = parent.History.CurrentId;
            await window.RefreshLinkedSources(verifyHash: true); await window.RefreshLinkedSources();
            Assert.Equal(undone, parent.History.CurrentId); Assert.Equal(SKColors.Blue, parent.Document.Find(id)!.Pixels!.GetPixel(20, 20));
            window.Settings.AutoUpdateLinkedSources = false; Write(SKColors.Green); await window.RefreshLinkedSources();
            Assert.Equal(SKColors.Blue, parent.Document.Find(id)!.Pixels!.GetPixel(20, 20));
            window.Settings.AutoUpdateLinkedSources = true; await window.RefreshLinkedSources();
            Assert.Equal(SKColors.Green, parent.Document.Find(id)!.Pixels!.GetPixel(20, 20));
            var unchanged = parent.History.CurrentId; File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(3));
            await window.RefreshLinkedSources(); Assert.Equal(unchanged, parent.History.CurrentId);
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Automatic_refresh_preserves_dirty_contents_and_local_cache_and_recovers_when_missing_file_returns()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-protection-" + Guid.NewGuid().ToString("N") + ".cmps");
        var window = new MainWindow { Width = 1000, Height = 740 }; window.Show();
        try
        {
            ProjectFile.Save(EditorSession.NewCanvas(80, 60, SKColors.Blue).Document, path);
            var parent = EditorSession.NewCanvas(240, 180); window.AddSession(parent); var layer = (await window.PlaceSmartObjectPath(path))!;
            var contents = window.OpenSmartObject(layer); contents.Fill(SKColors.Orange);
            ProjectFile.Save(EditorSession.NewCanvas(80, 60, SKColors.Red).Document, path);
            await window.RefreshLinkedSources(verifyHash: true);
            Assert.Equal(SKColors.Blue, layer.Pixels!.GetPixel(20, 20)); Assert.Equal(SKColors.Orange, contents.ActiveLayer!.Pixels!.GetPixel(20, 20));
            contents.Undo(); Assert.False(contents.IsModified); await window.RefreshLinkedSources();
            Assert.Equal(SKColors.Red, layer.Pixels!.GetPixel(20, 20));
            File.Delete(path); var stable = parent.History.CurrentId; await window.RefreshLinkedSources();
            Assert.Equal(stable, parent.History.CurrentId); Assert.Equal(SKColors.Red, layer.Pixels.GetPixel(20, 20));
            ProjectFile.Save(EditorSession.NewCanvas(80, 60, SKColors.Green).Document, path); await window.RefreshLinkedSources();
            Assert.Equal(SKColors.Green, layer.Pixels.GetPixel(20, 20));
            File.WriteAllText(path, "incomplete external save"); await window.RefreshLinkedSources(); await window.RefreshLinkedSources(verifyHash: true);
            Assert.Equal(SKColors.Green, layer.Pixels.GetPixel(20, 20)); Assert.Empty(window.OwnedWindows);
            ProjectFile.Save(EditorSession.NewCanvas(80, 60, SKColors.Green).Document, path); await window.RefreshLinkedSources();
            var edited = new EditorSession(layer.SmartObject!.OpenDocument()); edited.Fill(SKColors.Purple); parent.UpdateSmartObject(layer.SmartObject, edited.Document);
            ProjectFile.Save(EditorSession.NewCanvas(80, 60, SKColors.Yellow).Document, path); await window.RefreshLinkedSources(verifyHash: true);
            Assert.True(layer.SmartObject!.Link!.HasLocalEdits); Assert.Equal(SKColors.Purple, layer.Pixels.GetPixel(20, 20));
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Linked_contents_save_writes_a_snapshot_keeps_new_edits_dirty_and_preserves_external_conflicts()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-save-ui-" + Guid.NewGuid().ToString("N") + ".cmps");
        var window = new MainWindow { Width = 1100, Height = 780 }; window.Show();
        try
        {
            var original = EditorSession.NewCanvas(600, 400, SKColors.Blue); ProjectFile.Save(original.Document, path);
            var parent = EditorSession.NewCanvas(300, 200); window.AddSession(parent); var layer = (await window.PlaceSmartObjectPath(path))!;
            var contents = window.OpenSmartObject(layer); contents.Fill(SKColors.Red);
            var saving = window.SaveSmartObjectContents(contents); Assert.True(window.IsSaving(contents));
            contents.Fill(SKColors.Green); Assert.True(await saving);
            Assert.True(contents.IsModified); Assert.Equal(SKColors.Red, ProjectFile.Load(path).ActiveLayer!.Pixels!.GetPixel(20, 20));
            Assert.Equal(SKColors.Red, layer.Pixels!.GetPixel(20, 20));
            Assert.True(await window.SaveSmartObjectContents(contents)); Assert.False(contents.IsModified);
            Assert.Equal(SKColors.Green, ProjectFile.Load(path).ActiveLayer!.Pixels!.GetPixel(20, 20)); Assert.Equal(SKColors.Green, layer.Pixels!.GetPixel(20, 20));
            ProjectFile.Save(original.Document, path); contents.Fill(SKColors.Orange);
            await Assert.ThrowsAsync<IOException>(() => window.SaveSmartObjectContents(contents));
            Assert.True(contents.IsModified); Assert.Equal(SKColors.Orange, contents.ActiveLayer!.Pixels!.GetPixel(20, 20));
            Assert.Equal(SKColors.Blue, ProjectFile.Load(path).ActiveLayer!.Pixels!.GetPixel(20, 20)); Assert.Equal(SKColors.Green, layer.Pixels!.GetPixel(20, 20));
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Place_update_missing_relink_and_source_info_keep_shared_objects_and_undo()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-ui-" + Guid.NewGuid().ToString("N") + ".png"); var moved = path + ".png";
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        try
        {
            using var image = Pixels.NewColor(80, 60); image.Erase(SKColors.Blue); ImageFiles.Save(image, path, ExportFormat.Png);
            var parent = EditorSession.NewCanvas(320, 240, SKColors.White); window.AddSession(parent);
            var layer = (await window.PlaceSmartObjectPath(path))!; var source = layer.SmartObject!; var frame = layer.Transform;
            parent.DuplicateSelectedLayers(); var copy = parent.ActiveLayer!; Assert.Same(source, copy.SmartObject);
            using var changed = Pixels.NewColor(120, 90); changed.Erase(SKColors.Red); ImageFiles.Save(changed, path, ExportFormat.Png);
            Assert.True(await window.UpdateLinkedSmartObject(parent, source)); Assert.Same(layer.SmartObject, copy.SmartObject);
            Assert.Equal(SKColors.Red, layer.Pixels!.GetPixel(30, 20)); Assert.Equal(frame, layer.Transform);
            source = layer.SmartObject!; File.Move(path, moved);
            Assert.Equal(LinkedSourceState.Missing, LinkedSmartObjectFile.Probe(source.Link!).State);
            var menu = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => m.Header as string == "_Layer");
            menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Smart Objects").Items.OfType<MenuItem>()
                .Single(m => m.Header as string == "Linked Source Info…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (var i = 0; i < 200 && !window.OwnedWindows.Any(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var dialog = Assert.Single(window.OwnedWindows); Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("cached contents remain available") == true);
            Assert.True(Screenshots.Save(dialog, "linked-source-missing")); dialog.Close(true); Dispatcher.UIThread.RunJobs();
            Assert.True(await window.ReplaceSmartObjectPath(copy, moved, relink: true)); Assert.Equal(Path.GetFullPath(moved), layer.SmartObject!.Link!.Path);
            Assert.Equal(frame, layer.Transform); Assert.Same(layer.SmartObject, copy.SmartObject);
            Assert.True(Screenshots.Save(window, "linked-smart-object-layer"));
            parent.SetSmartObjectLink(layer.SmartObject!, null); Assert.Null(copy.SmartObject!.Link);
            parent.Undo(); Assert.NotNull(parent.Document.Find(copy.Id)!.SmartObject!.Link);
        }
        finally { window.Close(); File.Delete(path); File.Delete(moved); }
    }

    [AvaloniaFact]
    public async Task A_document_cannot_link_itself_and_stale_replacement_does_not_modify_the_target()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-ui-" + Guid.NewGuid().ToString("N") + ".cmps");
        var window = new MainWindow { Width = 1000, Height = 740 }; window.Show();
        try
        {
            var parent = EditorSession.NewCanvas(80, 60, SKColors.Blue); ProjectFile.Save(parent.Document, path); parent.MarkSaved(path); window.AddSession(parent);
            await Assert.ThrowsAsync<InvalidOperationException>(() => window.PlaceSmartObjectPath(path)); Assert.Single(parent.Document.Layers);
            var source = new EditorSession(ProjectFile.Load(path)); var layer = parent.PlaceSmartObject(source.Document, "Embedded");
            var old = layer.SmartObject!; parent.DuplicateSelectedLayers(); parent.SetSmartObjectLink(old, new(path + ".missing", 0, DateTime.UtcNow, new string('A', 64)));
            Assert.Throws<InvalidOperationException>(() => parent.ReplaceSmartObject(old, source.Document)); Assert.Equal(SKColors.Blue, layer.Pixels!.GetPixel(10, 10));
        }
        finally { window.Close(); File.Delete(path); }
    }
}

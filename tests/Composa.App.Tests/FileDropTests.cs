using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Core.Tests;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class FileDropTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dropping_on_tab_header_or_empty_strip_opens_images_in_order_without_changing_document(bool header)
    {
        WithImages((window, original, paths) =>
        {
            var bar = window.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "DocumentTabBar");
            var target = header ? bar.GetVisualDescendants().OfType<ScrollViewer>().First().GetVisualDescendants().OfType<Button>().First() : (Control)bar;
            using var data = Files(window, paths);
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Point(2, 2), KeyModifiers.None)
                { DragEffects = DragDropEffects.Copy | DragDropEffects.Move };
            target.RaiseEvent(over);
            Assert.True(over.Handled); Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            var drop = new DragEventArgs(DragDrop.DropEvent, data, target, new Point(2, 2), KeyModifiers.None);
            target.RaiseEvent(drop); Dispatcher.UIThread.RunJobs();
            Assert.True(drop.Handled);
            Assert.Equal(3, window.Sessions.Count);
            Assert.Single(original.Document.Layers); Assert.False(original.IsModified);
            Assert.Equal(31, window.Sessions[1].Document.Width);
            Assert.Equal(47, window.Sessions[2].Document.Width);
            Assert.Equal(SKColors.Red, window.Sessions[1].ActiveLayer!.Pixels!.GetPixel(0, 0));
            Assert.Equal(SKColors.Blue, window.Sessions[2].ActiveLayer!.Pixels!.GetPixel(0, 0));
            Assert.Same(window.Sessions[2], window.Session);
        });
    }

    [AvaloniaFact]
    public void Dropping_on_canvas_still_places_layers_and_can_be_undone()
    {
        WithImages((window, original, paths) =>
        {
            var canvas = window.GetVisualDescendants().OfType<CanvasView>().Single();
            using var data = Files(window, paths);
            canvas.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, canvas, new Point(150, 150), KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.Sessions); Assert.Same(original, window.Session);
            Assert.Equal(3, original.Document.Layers.Count);
            original.Undo(); original.Undo(); Assert.Single(original.Document.Layers); Assert.False(original.IsModified);
        });
    }

    [AvaloniaFact]
    public void Dropping_on_empty_window_opens_a_document()
    {
        WithImages((window, original, paths) =>
        {
            // A separate empty window exercises the welcome screen without any active document.
            var empty = new MainWindow(); empty.Show();
            try
            {
                using var data = Files(empty, paths.Take(1));
                empty.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, empty, new Point(200, 200), KeyModifiers.None));
                Dispatcher.UIThread.RunJobs();
                Assert.Single(empty.Sessions); Assert.Equal(31, empty.Session!.Document.Width);
            }
            finally { foreach (var session in empty.Sessions) session.MarkEmbeddedSaved(); empty.Close(); }
        });
    }

    private static DataTransfer Files(MainWindow window, IEnumerable<string> paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
            data.Add(DataTransferItem.CreateFile(window.StorageProvider.TryGetFileFromPathAsync(path).GetAwaiter().GetResult()!));
        return data;
    }

    private static void WithImages(Action<MainWindow, EditorSession, string[]> test)
    {
        var paths = new[] { Path.Combine(Path.GetTempPath(), $"composa-drop-{Guid.NewGuid():N}.png"),
            Path.Combine(Path.GetTempPath(), $"composa-drop-{Guid.NewGuid():N}.png") };
        var window = new MainWindow { Width = 1000, Height = 700 }; window.Show();
        var original = EditorSession.NewCanvas(120, 100, SKColors.White); window.AddSession(original);
        Dispatcher.UIThread.RunJobs();
        try
        {
            using var first = Pixels.NewColor(31, 23); first.Erase(SKColors.Red); ImageFiles.Save(first, paths[0], ExportFormat.Png);
            using var second = Pixels.NewColor(47, 35); second.Erase(SKColors.Blue); ImageFiles.Save(second, paths[1], ExportFormat.Png);
            test(window, original, paths);
        }
        finally
        {
            foreach (var session in window.Sessions) session.MarkEmbeddedSaved();
            window.Close(); foreach (var path in paths) TempFiles.Delete(path);
        }
    }
}

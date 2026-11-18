using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.IO;
using SkiaSharp;

namespace Composa.App.Tests;

public class DocumentReliabilityTests
{
    private static string Temp(string extension = "") => Path.Combine(Path.GetTempPath(), $"composa-{Guid.NewGuid():N}{extension}");

    [Fact]
    public void Preferences_fall_back_to_the_last_valid_backup_and_preserve_it_when_repaired()
    {
        var folder = Directory.CreateDirectory(Temp());
        var path = Path.Combine(folder.FullName, "settings.json");
        try
        {
            new Settings { JpegQuality = 78 }.SaveTo(path);
            new Settings { JpegQuality = 93 }.SaveTo(path);
            File.WriteAllText(path, "{");
            var recovered = Settings.LoadFrom(path);
            Assert.Equal(78, recovered.JpegQuality);
            Assert.NotNull(recovered.StorageNotice);
            recovered.JpegQuality = 81;
            recovered.SaveTo(path);
            Assert.Equal(81, Settings.LoadFrom(path).JpegQuality);
            File.Delete(path);
            Assert.Equal(78, Settings.LoadFrom(path).JpegQuality);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task Failed_autosave_retries_the_same_revision_and_keeps_the_recovered_copy()
    {
        var folder = Temp();
        try
        {
            File.WriteAllText(folder, "temporarily unavailable");
            var recovery = new Recovery(folder);
            var session = EditorSession.NewCanvas(24, 24, SKColors.White);
            session.Fill(SKColors.Red);
            await recovery.Save([session]);
            Assert.NotNull(recovery.Warning);
            File.Delete(folder);
            await recovery.Save([session]);
            Assert.Null(recovery.Warning);
            var path = Assert.Single(Directory.GetFiles(folder, "*.cmps"));
            var metadata = Assert.Single(Directory.GetFiles(folder, "*.json"));
            Assert.Equal(SKColors.Red, ProjectFile.Load(path).Layers[0].Pixels!.GetPixel(0, 0));
            var restored = new EditorSession(ProjectFile.Load(path));
            var next = new Recovery(folder);
            next.Adopt(restored, new(path, metadata, "Recovered", null, DateTime.Now));
            restored.Fill(SKColors.Blue);
            await next.Save([restored]);
            Assert.Equal(path, Assert.Single(Directory.GetFiles(folder, "*.cmps")));
            Assert.Equal(SKColors.Blue, ProjectFile.Load(path).Layers[0].Pixels!.GetPixel(0, 0));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); else File.Delete(folder); }
    }

    [AvaloniaFact]
    public async Task Closing_after_save_keeps_edits_made_while_writing()
    {
        var path = Temp(".cmps");
        var window = new MainWindow(); window.Show();
        var session = EditorSession.NewCanvas(2600, 2000, SKColors.White);
        try
        {
            session.FilePath = path;
            window.AddSession(session);
            session.Fill(SKColors.Red);
            window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
            Assert.Single(window.OwnedWindows).Close(true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsSaving(session));
            session.Fill(SKColors.Blue);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (window.IsSaving(session)) await Task.Delay(5, timeout.Token);
            Assert.Contains(session, window.Sessions);
            Assert.True(session.IsModified);
            Assert.Equal(SKColors.Red, ProjectFile.Load(path).Layers[0].Pixels!.GetPixel(0, 0));
            Assert.Equal(SKColors.Blue, session.ActiveLayer!.Pixels!.GetPixel(0, 0));
        }
        finally { session.MarkEmbeddedSaved(); window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Concurrent_open_aliases_share_a_tab_and_conflicting_writes_keep_the_external_file()
    {
        var path = Temp(".cmps");
        var window = new MainWindow(); window.Show();
        try
        {
            ProjectFile.Save(EditorSession.NewCanvas(32, 32, SKColors.White).Document, path);
            var alias = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path));
            var opened = await Task.WhenAll(window.OpenPath(path), window.OpenPath(alias));
            var session = Assert.Single(window.Sessions);
            Assert.All(opened, result => Assert.Same(session, result));
            var other = EditorSession.NewCanvas(10, 10); window.AddSession(other);
            Assert.IsType<IOException>(await window.SaveTo(other, alias));
            File.WriteAllText(path, "external edit");
            Assert.IsType<IOException>(await window.SaveTo(session, path));
            Assert.Equal("external edit", File.ReadAllText(path));
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaTheory]
    [InlineData(ExportFormat.Png, ".png")]
    [InlineData(ExportFormat.Jpeg, ".jpg")]
    [InlineData(ExportFormat.Tiff, ".tif")]
    public async Task Export_and_reopen_preserve_print_resolution(ExportFormat format, string extension)
    {
        var path = Temp(extension);
        var window = new MainWindow(); window.Show();
        try
        {
            using var bitmap = Composa.Rendering.Pixels.NewColor(32, 24); bitmap.Erase(SKColors.White);
            ImageFiles.Save(bitmap, path, format, resolution: 300);
            var reopened = await window.OpenPath(path);
            Assert.InRange(reopened!.Document.Resolution, 299.98, 300.02);
            var original = File.ReadAllBytes(path);
            Assert.ThrowsAny<OperationCanceledException>(() => ImageFiles.Save(bitmap, path, format, resolution: 72, cancellationToken: new(true)));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { window.Close(); File.Delete(path); }
    }
    [AvaloniaTheory]
    [InlineData(900, 600)]
    [InlineData(1280, 720)]
    public void Compact_tools_and_dock_sections_stay_reachable(int width, int height)
    {
        var window = new MainWindow { Width = width, Height = height }; window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(160, 120, SKColors.White));
            window.SelectTool(Tool.Text);
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            foreach (var name in new[] { "Navigator", "Channels", "History" }) dock.SetVisible(dock.Section(name), true);
            Dispatcher.UIThread.RunJobs();
            var viewer = dock.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "DockSectionScroll");
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height);
            viewer.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            var layers = dock.GetVisualDescendants().OfType<LayersPanel>().Single();
            Assert.InRange(layers.TranslatePoint(new Point(), window)!.Value.Y, 0, height / 2);
            Assert.True(layers.Bounds.Height > 175);
            var header = dock.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) is "Collapse History");
            var point = header.TranslatePoint(new Point(4, 4), window)!.Value;
            Assert.InRange(point.Y, 0, height - 1);
            header.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.True(dock.Section("History").State.Collapsed);
            if (width == 900)
            {
                var more = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "More…");
                Assert.True(more.IsVisible);
                Assert.NotNull(more.Flyout);
            }
        }
        finally { window.Close(); }
    }
}

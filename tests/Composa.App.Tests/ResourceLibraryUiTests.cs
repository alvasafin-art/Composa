using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class ResourceLibraryUiTests
{
    [AvaloniaFact]
    public async Task Save_search_keyboard_insert_drop_and_reopen_two_cards_after_moving_the_library()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-library-ui-" + Guid.NewGuid().ToString("N"));
        var libraryFolder = Path.Combine(folder, "Library");
        Directory.CreateDirectory(folder);
        var app = Assert.IsType<App>(Application.Current);
        var previousLanguage = Locale.Language;
        Locale.Language = "ru";
        app.ApplyTheme(EditorTheme.DarkNeutral);
        MainWindow NewWindow() => new(null, url => new Composa.App.AI.ComfyClient(url), libraryFolder) { Width = 1280, Height = 720 };
        var window = NewWindow();
        MainWindow? reopenedWindow = null;
        var source = EditorSession.NewCanvas(640, 420, new SKColor(236, 222, 199));
        try
        {
            window.Show();
            window.AddSession(source);
            var photoPath = Environment.GetEnvironmentVariable("COMPOSA_LAYOUT_PREVIEW_PHOTO");
            var pixels = Pixels.NewColor(170, 102);
            using (var draw = new SKCanvas(pixels))
            {
                if (File.Exists(photoPath))
                {
                    using var photoFile = ImageFiles.Load(photoPath!);
                    draw.DrawBitmap(photoFile, new SKRect(0, 0, 170, 102));
                }
                else draw.Clear(new SKColor(144, 105, 78));
            }
            var photo = source.AddImageLayer("Фото", pixels, new(320, 150), fit: false);
            var heading = source.AddText(new(235, 215), new TextStyle { Text = "Высокогорный кофе", FontFamily = "Arial", Size = 17, Color = 0xff45291d });
            var price = source.AddText(new(235, 248), new TextStyle { Text = "690 ₽", FontFamily = "Arial", Size = 21, Color = 0xff805131 });
            source.SelectLayer(photo.Id);
            source.SelectLayer(heading.Id, extend: true);
            source.SelectLayer(price.Id, extend: true);
            Assert.True(source.CreateAutoLayout(source.ProposeAutoLayout(out _)!));
            var card = source.ActiveLayer!;
            source.SetAutoLayout(card, card.AutoLayout! with { Gap = 12 });
            source.SetAutoLayoutItem(photo, new(LayoutSizeMode.Fixed, LayoutSizeMode.Fixed, 170, 102));
            source.CreateLayerStyle(heading, LayerStyleKind.Text, "Заголовок");
            source.SelectLayer(card.Id);
            window.SelectTool(Tool.Move);
            Dispatcher.UIThread.RunJobs();
            Assert.False(Directory.Exists(libraryFolder));
            Click(Find<Button>(window, "SaveLayoutToLibrary"));
            await Wait(window, () => window.OwnedWindows.Any());
            var dialog = Assert.Single(window.OwnedWindows);
            Find<TextBox>(dialog, "LibraryResourceName").Text = "Кофе · карточка";
            Assert.True(Screenshots.Save(dialog, "library-save-card"));
            Click(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => button.IsDefault));
            await Wait(window, () => Tiles(window).Any(tile => tile.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap)));
            Assert.True(Screenshots.Save(window, "library-resources-1280"));
            window.Width = 1920;
            window.Height = 1080;
            await Wait(window, () => window.ClientSize.Width >= 1900);
            Assert.True(Screenshots.Save(window, "library-resources-1920"));
            window.Width = 1280;
            window.Height = 720;

            var projectA = Path.Combine(folder, "A.cmps");
            Assert.Null(await window.SaveTo(source, projectA));
            await CloseProject(window);

            var target = EditorSession.NewCanvas(720, 420, new SKColor(236, 222, 199));
            window.AddSession(target);
            var search = Find<TextBox>(window, "SearchLibrary");
            search.Text = "кисти";
            await Wait(window, () => !Tiles(window).Any(tile => tile.IsEffectivelyVisible));
            search.Text = "кофе";
            await Wait(window, () => Tiles(window).Any());
            search.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            await Wait(window, () => Tiles(window).Any(tile => tile.IsFocused));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Wait(window, () => target.Document.Layers.Count == 2);
            var first = target.ActiveLayer!;
            Assert.True(Screenshots.Save(window, "library-first-insert"));
            var beforeDrop = target.Revision;
            var entry = Assert.IsType<LibraryEntry>(Assert.Single(Tiles(window)).Tag);
            using var data = ResourceLibraryPanel.DragData(entry.Id);
            var point = window.Canvas.ToScreen(new SKPoint(563, 210));
            var drop = new DragEventArgs(DragDrop.DropEvent, data, window.Canvas, point, KeyModifiers.None) { DragEffects = DragDropEffects.Copy };
            window.Canvas.RaiseEvent(drop);
            Assert.True(drop.Handled);
            await Wait(window, () => target.Revision > beforeDrop);
            var second = target.ActiveLayer!;
            Assert.Equal(563, EditorSession.LayoutBounds(second).MidX, 0);
            Assert.Equal(12, second.AutoLayout!.Gap);
            Assert.Single(target.Document.Styles);
            var head = second.Children.Single(layer => layer.Text?.Text == "Высокогорный кофе");
            var imageLayer = first.Children.Single(layer => layer.LayoutFrame != null);
            var newImage = Pixels.NewColor(88, 120);
            newImage.Erase(new SKColor(124, 141, 112));
            if (File.Exists(photoPath))
            {
                using var file = ImageFiles.Load(photoPath!);
                using var draw = new SKCanvas(newImage);
                draw.RotateDegrees(180, 44, 60);
                draw.DrawBitmap(file, new SKRect(0, 0, 88, 120));
            }
            Assert.True(target.ReplaceLayoutImage(imageLayer, newImage));
            var editor = target.EditText(head)!;
            editor.Insert("\nИз Гватемалы");
            Assert.True(target.FinishText());
            var projectB = Path.Combine(folder, "B.cmps");
            Assert.Null(await window.SaveTo(target, projectB));
            Assert.True(Screenshots.Save(window, "library-two-edited-cards"));
            var output = Environment.GetEnvironmentVariable("COMPOSA_LIBRARY_PROOF");
            if (output != null) File.Copy(projectB, output, overwrite: true);
            window.Close();
            Directory.Move(libraryFolder, Path.Combine(folder, "Offline Library"));
            reopenedWindow = NewWindow();
            reopenedWindow.Show();
            var reopened = Assert.IsType<EditorSession>(await reopenedWindow.OpenPath(projectB));
            Assert.Equal(2, reopened.Document.Layers.Count(layer => layer.IsGroup));
            Assert.Equal("Высокогорный кофе\nИз Гватемалы", reopened.Document.Find(head.Id)?.Text?.Text);
            Assert.Equal(88, reopened.Document.Find(imageLayer.Id)?.Pixels?.Width);
            Assert.True(Screenshots.Save(reopenedWindow, "library-reopened-offline"));
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            foreach (var session in window.Sessions) session.MarkSaved("test.cmps");
            if (window.IsVisible) window.Close();
            reopenedWindow?.Close();
            Locale.Language = previousLanguage;
            app.ApplyTheme(EditorTheme.Classic);
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_shelf_of_200_resources_loads_previews_only_around_the_viewport()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-library-scroll-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new ElementLibrary(folder);
            var source = EditorSession.NewCanvas(280, 210);
            var pixels = Pixels.NewColor(210, 128);
            var photoPath = Environment.GetEnvironmentVariable("COMPOSA_LAYOUT_PREVIEW_PHOTO");
            using (var draw = new SKCanvas(pixels))
            {
                if (File.Exists(photoPath))
                {
                    using var image = ImageFiles.Load(photoPath!);
                    draw.DrawBitmap(image, new SKRect(0, 0, 210, 128));
                }
                else draw.Clear(new SKColor(144, 105, 78));
            }
            var photo = source.AddImageLayer("Photo", pixels, new(110, 70), fit: false);
            var title = source.AddText(new(12, 152), new TextStyle { Text = "Coffee card", Size = 20 });
            source.SelectLayer(photo.Id);
            source.SelectLayer(title.Id, extend: true);
            Assert.True(source.CreateAutoLayout(source.ProposeAutoLayout(out _)!));
            var capture = source.CaptureLibraryElement(out _);
            var thumb = ElementLibrary.RenderThumbnail(capture);
            await Task.Run(() =>
            {
                for (int i = 1; i <= 200; i++) library.Save($"Card {i:000}", capture, thumb, false);
            });
            var project = Path.Combine(folder, "catalog.cmps");
            ProjectFile.Save(EditorSession.NewCanvas(720, 420, SKColors.White).Document, project);
            var output = Environment.GetEnvironmentVariable("COMPOSA_LIBRARY_PERFORMANCE");
            var passes = output == null ? 1 : 5;
            var results = new List<object>();
            using var process = Process.GetCurrentProcess();
            double PrivateMiB() { process.Refresh(); return process.PrivateMemorySize64 / 1048576.0; }
            static void Frame()
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
            static double Elapsed(long stamp) => Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            for (int pass = 1; pass <= passes; pass++)
            {
                var start = Stopwatch.GetTimestamp();
                var window = new MainWindow(null, url => new Composa.App.AI.ComfyClient(url), folder) { Width = 1280, Height = 720 };
                try
                {
                    window.Show();
                    var target = Assert.IsType<EditorSession>(await window.OpenPath(project));
                    Frame();
                    var windowMs = Elapsed(start);
                    var closedPrivate = PrivateMiB();
                    Assert.Empty(Tiles(window));
                    Assert.DoesNotContain(window.GetVisualDescendants(), view => view is ResourceLibraryPanel);
                    start = Stopwatch.GetTimestamp();
                    Click(Find<Button>(window, "ShowLibraryResources"));
                    await Wait(window, () => Tiles(window).Any(HasPreview));
                    Frame();
                    var firstShelfMs = Elapsed(start);
                    var openedPrivate = PrivateMiB();
                    Assert.InRange(Tiles(window).Count(HasPreview), 1, 10);
                    var endToEnd = new List<double>();
                    var frameUpdates = new List<double>();
                    var scrollPrivate = new List<double> { openedPrivate };
                    var moves = output == null ? 2 : 20;
                    for (int i = 0; i < moves; i++)
                    {
                        Assert.True(Tiles(window).First(tile => tile.IsEffectivelyVisible).Focus());
                        var toEnd = i % 2 == 0;
                        var expected = toEnd ? "Card 200" : "Card 001";
                        start = Stopwatch.GetTimestamp();
                        window.KeyPressQwerty(toEnd ? PhysicalKey.End : PhysicalKey.Home, RawInputModifiers.None);
                        await Wait(window, () => Tiles(window).Any(tile => tile.Tag is LibraryEntry entry && entry.Name == expected && HasPreview(tile)));
                        frameUpdates.Add(Screenshots.RenderAndReadback(window));
                        endToEnd.Add(Elapsed(start));
                        scrollPrivate.Add(PrivateMiB());
                        Assert.InRange(Tiles(window).Count(HasPreview), 1, 10);
                    }
                    var tile = Tiles(window).First(HasPreview);
                    tile.Focus();
                    start = Stopwatch.GetTimestamp();
                    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                    await Wait(window, () => target.Document.Layers.Count == 2);
                    Frame();
                    var insertMs = Elapsed(start);
                    var insertPrivate = PrivateMiB();
                    Click(window.GetVisualDescendants().OfType<LayersPanel>().Single().PanelDragHandle);
                    await Wait(window, () => !Tiles(window).Any());
                    target.MarkSaved(project);
                    await CloseProject(window);
                    Frame();
                    var afterClosePrivate = PrivateMiB();
                    results.Add(new { pass, windowMs, firstShelfMs, endToEnd, frameUpdates, insertMs, closedPrivate, openedPrivate,
                        afterClosePrivate, sampledPeakMiB = Math.Max(insertPrivate, scrollPrivate.Max()) });
                }
                finally
                {
                    foreach (var item in window.Sessions) item.MarkSaved(project);
                    window.Close();
                }
            }
            if (output != null) File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static bool HasPreview(Button tile) => tile.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap);

    private static IEnumerable<Button> Tiles(Window window) => window.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "LibraryResource");

    private static T Find<T>(Window window, string name) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static void Click(Control target) =>
        target.RaiseEvent(new RoutedEventArgs(target is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));

    private static async Task CloseProject(MainWindow window)
    {
        var tab = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border => border.ContextMenu?.Items
            .OfType<MenuItem>().Any(item => Equals(item.Header, "Close Others")) == true);
        Click(Assert.Single(tab.ContextMenu!.Items.OfType<MenuItem>(), item => Equals(item.Header, "Close")));
        await Wait(window, () => window.Sessions.Count == 0);
    }

    private static async Task Wait(Window window, Func<bool> ready)
    {
        for (int i = 0; i < 200; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (ready()) return;
            await Task.Delay(20);
        }
        Assert.True(ready(), "Resource library action did not finish.");
    }
}

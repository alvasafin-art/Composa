using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class CanvasLayoutUiTests
{
    [AvaloniaFact]
    public async Task Compact_new_canvas_leaves_room_for_the_settings_scrollbar()
    {
        var window = new MainWindow();
        window.Show();
        var creation = CanvasDialogs.NewCanvas(window, SKColors.White);
        var dialog = window.OwnedWindows.Single();
        try
        {
            dialog.Height = 560;
            Dispatcher.UIThread.RunJobs();
            Click(dialog, "LayoutColumns");
            Find<NumericUpDown>(dialog, "LayoutCount").Value = 4;
            Assert.True(Screenshots.Save(dialog, "new-canvas-scrollbar"));

            var scroll = Find<ScrollViewer>(dialog, "NewCanvasSettings");
            Assert.True(scroll.AllowAutoHide);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                .Single(control => control.Name == "PART_VerticalScrollBar" && control.TemplatedParent == scroll);
            Assert.True(bar.IsVisible);
            var content = Assert.IsAssignableFrom<Control>(scroll.Content);
            var actions = Find<Grid>(dialog, "NewCanvasActions");
            var link = Find<ToggleButton>(dialog, "LinkSafeArea");
            double Left(Control control) => control.TranslatePoint(default, dialog)!.Value.X;
            double Right(Control control) => Left(control) + control.Bounds.Width;
            Assert.Equal(Left(content), Left(actions), 3);
            Assert.Equal(Right(content), Right(actions), 3);
            Assert.Equal(Right(content), Right(link), 3);
            Assert.True(Right(content) < Left(bar));
            dialog.MouseMove(bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), dialog)!.Value);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Right(content) < Left(bar));
        }
        finally
        {
            dialog.Close(false);
            await creation;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Canvas_dimensions_commit_from_properties_and_swap_in_one_undo_step()
    {
        var window = new MainWindow { Width = 1280, Height = 850 };
        window.Show();
        var session = EditorSession.NewCanvas(300, 200);
        window.AddSession(session);
        var text = session.AddText(new SKPoint(64, 50), new TextStyle { Text = "Headline" });
        var placement = text.ControlBounds;
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "CanvasPropertiesTarget");
            Assert.Empty(window.GetVisualDescendants().OfType<CanvasLayoutEditor>());
            DeselectOnPasteboard(window, session);
            var width = Find<NumericUpDown>(window, "CanvasPropertyWidth");
            var input = width.GetVisualDescendants().OfType<TextBox>().Single();
            input.Focus();
            input.Text = "360";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal(360, session.Document.Width);
            Find<NumericUpDown>(window, "CanvasPropertyResolution").Value = 300;
            Assert.Equal(300, session.Document.Resolution);
            var history = session.History.Count;
            Click(window, "SwapCanvasPropertiesDimensions");
            Assert.Equal((200, 360), (session.Document.Width, session.Document.Height));
            Assert.Equal(history + 1, session.History.Count);
            Assert.Equal(placement, text.ControlBounds);
            Assert.NotNull(text.Text);
            session.Undo();
            Assert.Equal((360, 200), (session.Document.Width, session.Document.Height));
            Assert.Equal(360m, width.Value);
            session.SelectLayer(text.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.GetVisualDescendants().OfType<CanvasLayoutEditor>());
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Headline");
        }
        finally { session.MarkSaved("document.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Custom_and_recent_presets_restore_the_layout_from_preferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-layout-preset-" + Guid.NewGuid().ToString("N") + ".json");
        var window = new MainWindow();
        window.Show();
        try
        {
            var creation = CanvasDialogs.NewCanvas(window, SKColors.White, settings: window.Settings);
            var dialog = window.OwnedWindows.Single();
            Click(dialog, "LayoutColumns");
            Assert.Null(Find<NumericUpDown>(dialog, "LayoutCount").Value);
            Find<NumericUpDown>(dialog, "LayoutCount").Value = 12;
            Click(dialog, "LayoutRows");
            Assert.Null(Find<NumericUpDown>(dialog, "LayoutCount").Value);
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save preset"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var prompt = dialog.OwnedWindows.Single();
            prompt.GetVisualDescendants().OfType<TextBox>().Single().Text = "Editorial card";
            prompt.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => window.Settings.UserCanvasPresets.Count == 1);
            dialog.Close(true);
            var created = await creation;
            Assert.NotNull(created);
            window.Settings.SaveTo(path);
            var restored = Settings.LoadFrom(path);
            var recent = Assert.Single(restored.RecentCanvasPresets);
            var custom = Assert.Single(restored.UserCanvasPresets);
            Assert.Equal(("Editorial card", created), (custom.Name, custom.Options));
            Assert.Equal(custom, recent);
            Assert.Equal(12, custom.Options.Layout.Columns?.Count);
            Assert.Equal(0, custom.Options.Layout.Rows?.Count);
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task New_card_snaps_text_edits_grid_reopens_and_exports_without_guides()
    {
        var temp = Path.Combine(Path.GetTempPath(), "composa-layout-" + Guid.NewGuid().ToString("N"));
        var path = temp + ProjectFile.Extension;
        var png = temp + ".png";
        var window = new MainWindow { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control);
            await Until(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            var design = dialog.GetVisualDescendants().OfType<NewCanvasView>().Single();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "Portrait card"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((1080, 1350), (design.Options.Width, design.Options.Height));
            Assert.Equal(CanvasLayout.Empty, design.Options.Layout);
            Assert.True(Screenshots.Save(dialog, "new-canvas-layout"));
            var safeTop = Find<NumericUpDown>(dialog, "Topinset");
            Assert.Null(safeTop.Value);
            Assert.Equal("0", safeTop.PlaceholderText);
            safeTop.Value = 64;
            Click(dialog, "LayoutSquares");
            var square = Find<NumericUpDown>(dialog, "LayoutSquareSize");
            Assert.Null(square.Value);
            Assert.Equal(0, design.Options.Layout.SquareSize);
            Click(dialog, "RemoveLayoutGrid");
            Click(dialog, "LayoutColumns");
            var count = Find<NumericUpDown>(dialog, "LayoutCount");
            Assert.Null(count.Value);
            Assert.Null(design.Options.Layout.Columns!.Geometry(1080));
            count.Value = 4;
            count.Value = null;
            Assert.Null(design.Options.Layout.Columns.Geometry(1080));
            count.Value = 4;
            Assert.Equal(new CanvasTracks(), design.Options.Layout.Columns);
            Assert.True(Screenshots.Save(dialog, "new-canvas-configured-grid"));
            Find<ComboBox>(dialog, "NewCanvasColorMode").SelectedIndex = (int)CanvasColorMode.CMYK;
            Assert.Equal(CanvasColorMode.CMYK, design.Options.ColorMode);
            dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault).Focus();
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => window.Session != null);
            var session = window.Session!;
            Assert.Equal(new CanvasTracks(), session.Document.Layout.Columns);

            // Text is moved by the normal canvas Move tool, close enough to the first column to snap.
            var text = session.AddText(new SKPoint(109, 260), new TextStyle { Text = "Headline", Size = 80 });
            window.SelectTool(Tool.Move);
            session.View = session.View with { SnapToLayers = false, SnapToDocumentBounds = false };
            Dispatcher.UIThread.RunJobs();
            var origin = text.ControlBounds;
            var from = At(origin.Left + 25, origin.MidY);
            var to = At(68 + 25, origin.MidY);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(new Point((from.X + to.X) / 2, to.Y), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(64, text.ControlBounds.Left, 3);

            DeselectOnPasteboard(window, session);
            var editor = window.GetVisualDescendants().OfType<CanvasLayoutEditor>().Single();
            var margin = Find<NumericUpDown>(editor, "LayoutMargin");
            Assert.Equal(64m, margin.Value);
            margin.Value = 80;
            Assert.Equal(80, session.Document.Layout.Columns!.Margin);
            Find<NumericUpDown>(editor, "Topinset").Value = 72;
            Assert.Equal(new CanvasMargins(72), session.Document.Layout.SafeArea);
            var link = Find<ToggleButton>(editor, "LinkSafeArea");
            link.IsChecked = false;
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<NumericUpDown>(editor, "Rightinset").Value = 82;
            Click(editor, "LayoutRows");
            Find<NumericUpDown>(editor, "LayoutCount").Value = 4;
            Find<ComboBox>(editor, "LayoutSizing").SelectedIndex = (int)GridSizing.Fixed;
            Find<NumericUpDown>(editor, "LayoutTrackSize").Value = 1;
            Find<NumericUpDown>(editor, "LayoutGap").Value = 7;
            Assert.Equal(8, session.Document.Layout.Rows!.Geometry(1350)!.Value.Position(1) -
                session.Document.Layout.Rows.Geometry(1350)!.Value.Position(0));
            Click(editor, "LayoutSquares");
            Assert.Null(Find<NumericUpDown>(editor, "LayoutSquareSize").Value);
            Find<NumericUpDown>(editor, "LayoutSquareSize").Value = 8;
            Assert.Equal(8, session.Document.Layout.SquareSize);
            Click(editor, "RemoveLayoutGrid");
            Assert.Null(session.Document.Layout.SquareSize);
            var bleed = Find<NumericUpDown>(editor, "LayoutBleed");
            Assert.Null(bleed.Value);
            Assert.Equal("0", bleed.PlaceholderText);
            bleed.Value = 3;
            Assert.Equal(3, session.Document.Layout.BleedMm);
            Assert.True(Screenshots.Save(window, "canvas-layout-properties"));
            var layout = session.Document.Layout;
            var found = session.SnapPoint(new SKPoint(82, 15), 4);
            Assert.Equal(80, found.SnapX);
            session.View = session.View with { ShowCanvasLayout = false };
            Assert.Equal(found, session.SnapPoint(new SKPoint(82, 15), 4));
            session.View = session.View with { SnapToCanvasLayout = false };
            Assert.Null(session.SnapPoint(new SKPoint(82, 15), 4).SnapX);

            Assert.Null(await window.SaveTo(session, path));
            var close = window.GetLogicalDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "Close Project"));
            close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => window.Session == null);
            var reopened = await window.OpenPath(path);
            Assert.NotNull(reopened);
            Assert.Equal(layout, reopened.Document.Layout);
            Assert.Equal(CanvasColorMode.CMYK, reopened.Document.ColorMode);
            Assert.Equal(new CanvasMargins(72, 82, 72, 72), reopened.Document.Layout.SafeArea);
            Assert.Equal(64, reopened.Document.AllLayers().Single(layer => layer.Text != null).ControlBounds.Left, 3);

            using var export = reopened.Flatten();
            ImageFiles.Save(export, png, ExportFormat.Png, resolution: reopened.Document.Resolution);
            using var result = SKBitmap.Decode(png);
            Assert.Equal((1080, 1350), (result.Width, result.Height));
            Assert.Equal(SKColors.White, result.GetPixel(80, 500));
            Assert.Equal(SKColors.White, result.GetPixel(500, 72));
            var designPixels = result.Pixels;
            Assert.Contains(designPixels, pixel => pixel.Red < 80);
            var before = File.ReadAllBytes(png);
            reopened.View = reopened.View with { ShowCanvasLayout = !reopened.View.ShowCanvasLayout };
            using var repeat = reopened.Flatten();
            ImageFiles.Save(repeat, png, ExportFormat.Png, resolution: reopened.Document.Resolution);
            Assert.Equal(before, File.ReadAllBytes(png));
        }
        finally
        {
            window.Close();
            File.Delete(path);
            File.Delete(png);
        }

        Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(Visual root, string name) => Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void DeselectOnPasteboard(MainWindow window, EditorSession session)
    {
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        var empty = window.Canvas.TranslatePoint(new Point(8, 8), window)!.Value;
        window.MouseDown(empty, MouseButton.Left);
        window.MouseUp(empty, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(session.ActiveLayer);
        Assert.Empty(session.Document.SelectedLayerIds);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 60 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Assert.True(condition());
    }
}

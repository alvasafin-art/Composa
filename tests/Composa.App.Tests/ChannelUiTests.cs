using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using Composa.IO;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class ChannelUiTests
{
    [AvaloniaFact]
    public void Escape_cancels_channel_number_edit_and_scrub_and_printable_keys_remain_unhandled()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(120, 100, SKColors.CornflowerBlue); window.AddSession(session);
            session.AddAlphaChannel("Object"); session.SelectRect(new SKRect(20, 20, 40, 30)); session.Fill(SKColors.White); session.Deselect();
            var original = session.Document.AlphaChannels["Object"]; var history = session.History.Count;
            window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
            var angle = window.GetVisualDescendants().OfType<NumericUpDown>().ElementAt(4); var input = angle.GetVisualDescendants().OfType<TextBox>().Single();
            input.Focus(); input.SelectAll(); var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D9 }; input.RaiseEvent(key); Assert.False(key.Handled);
            window.KeyTextInput("9"); Dispatcher.UIThread.RunJobs(); Assert.NotNull(session.Transform);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.Null(session.Transform); Assert.Same(original, session.Document.AlphaChannels["Object"]); Assert.Equal(history, session.History.Count);
            var label = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "∠"); var at = label.TranslatePoint(new Point(5, 5), window)!.Value;
            window.MouseDown(at, MouseButton.Left); window.MouseMove(at + new Vector(20, 0), RawInputModifiers.LeftMouseButton); Assert.NotNull(session.Transform);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            window.MouseMove(at + new Vector(30, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(at + new Vector(30, 0), MouseButton.Left);
            Assert.False(window.IsDragging); Assert.Null(session.Transform); Assert.Same(original, session.Document.AlphaChannels["Object"]); Assert.Equal(history, session.History.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Channel_inspector_keeps_one_original_for_typed_angles_and_commits_before_saving()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-channel-inspector-" + Guid.NewGuid().ToString("N") + ".cmps");
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(120, 100, SKColors.CornflowerBlue); window.AddSession(session);
            session.AddAlphaChannel("Object"); session.SelectRect(new SKRect(20, 20, 40, 30)); session.Fill(SKColors.White); session.Deselect();
            var original = session.Document.AlphaChannels["Object"]; var before = original.Bytes; var history = session.History.Count;
            window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
            var angle = window.GetVisualDescendants().OfType<NumericUpDown>().ElementAt(4);
            var input = angle.GetVisualDescendants().OfType<TextBox>().Single(); input.Focus(); input.SelectAll();
            window.KeyTextInput("9"); Dispatcher.UIThread.RunJobs(); Assert.NotNull(session.Transform); Assert.Equal(9, session.ChannelControlTransform.Rotation);
            window.KeyTextInput("0"); Dispatcher.UIThread.RunJobs(); Assert.Equal(90, session.ChannelControlTransform.Rotation); Assert.Equal(history, session.History.Count);
            Assert.Equal(before, original.Bytes); Assert.Equal(new SKRectI(25, 15, 35, 35), SelectionMask.Bounds(session.Document.AlphaChannels["Object"]));
            Assert.Null(await window.SaveTo(session, path)); Assert.Null(session.Transform); Assert.False(session.IsModified); Assert.Equal(history + 1, session.History.Count);
            Assert.Equal(new SKRectI(25, 15, 35, 35), SelectionMask.Bounds(ProjectFile.Load(path).AlphaChannels["Object"]));
            session.Undo(); Assert.Same(original, session.Document.AlphaChannels["Object"]); Assert.Equal(before, original.Bytes);
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public void Moving_channel_contents_and_alt_duplicate_keep_the_image_and_use_current_content_bounds()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 240, SKColors.CornflowerBlue); window.AddSession(session);
            var image = session.ActiveLayer!.Pixels!; var frame = session.ActiveLayer.Transform;
            session.AddAlphaChannel("Object"); session.SelectRect(new SKRect(100, 80, 160, 120)); session.Fill(SKColors.White); session.Deselect();
            var original = session.Document.AlphaChannels["Object"]; var bytes = original.Bytes;
            OpenChannels(window); session.SetChannelView(RgbChannels.All, alphaOverlay: true); window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            window.MouseDown(At(130, 100), MouseButton.Left); window.MouseMove(At(170, 120), RawInputModifiers.LeftMouseButton); window.MouseUp(At(170, 120), MouseButton.Left);
            Assert.Equal(140, session.ChannelControlTransform.X); Assert.Equal(100, session.ChannelControlTransform.Y);
            Assert.Equal(0, session.Document.AlphaChannels["Object"].GetPixel(110, 90).Alpha); Assert.Equal(255, session.Document.AlphaChannels["Object"].GetPixel(150, 110).Alpha);
            Assert.Same(image, session.ActiveLayer.Pixels); Assert.Equal(frame, session.ActiveLayer.Transform); Assert.Equal(bytes, original.Bytes);
            Dispatcher.UIThread.RunJobs(); var x = window.GetVisualDescendants().OfType<NumericUpDown>().First(); Assert.Equal(140m, x.Value);
            x.Value = 150; Dispatcher.UIThread.RunJobs(); Assert.Equal(150, session.ChannelControlTransform.X);
            window.MouseDown(At(180, 120), MouseButton.Left, RawInputModifiers.Alt); window.MouseMove(At(260, 120), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt); window.MouseUp(At(260, 120), MouseButton.Left, RawInputModifiers.Alt);
            Assert.Equal(255, session.Document.AlphaChannels["Object"].GetPixel(160, 110).Alpha); Assert.Equal(255, session.Document.AlphaChannels["Object"].GetPixel(240, 110).Alpha);
            Assert.Equal(0, session.Document.AlphaChannels["Object"].GetPixel(220, 110).Alpha);
            Assert.Same(image, session.ActiveLayer.Pixels); Assert.Equal(frame, session.ActiveLayer.Transform); Assert.True(Screenshots.Save(window, "channels-transformed-alpha"));
            session.Undo(); Assert.Equal(0, session.Document.AlphaChannels["Object"].GetPixel(240, 110).Alpha);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Idle_channels_panel_does_not_rebuild_itself_after_layout()
    {
        var window = new MainWindow { Width = 1100, Height = 900 }; window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(320, 240, SKColors.Blue)); OpenChannels(window);
            await Task.Delay(200); Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<ChannelsPanel>().Single();
            var green = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ChannelGreen");
            await Task.Delay(350); Dispatcher.UIThread.RunJobs();
            Assert.Same(green, panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ChannelGreen"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Channels_panel_selects_a_component_and_brush_paints_it_while_the_RGB_preview_remains_available()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 240, new SKColor(80, 120, 200)); window.AddSession(session); var original = session.ActiveLayer!.Pixels!; var before = original.Bytes;
            OpenChannels(window); var panel = window.GetVisualDescendants().OfType<ChannelsPanel>().Single();
            var green = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ChannelGreen");
            Click(window, green, new(115, 17)); Assert.Equal(RgbChannels.Green, session.EditingRgbChannels);
            window.SelectTool(Tool.Brush); session.Foreground = SKColors.White; session.Brush = session.Brush with { Size = 25, Hardness = 1 };
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            window.MouseDown(At(100, 80), MouseButton.Left); window.MouseMove(At(180, 80), RawInputModifiers.LeftMouseButton); window.MouseUp(At(180, 80), MouseButton.Left);
            Assert.Equal(new SKColor(80, 255, 200), session.ActiveLayer!.Pixels!.GetPixel(140, 80)); Assert.Equal(before, original.Bytes);
            session.SetChannelView(RgbChannels.All); Dispatcher.UIThread.RunJobs();
            Assert.Equal(RgbChannels.Green, session.EditingRgbChannels); Assert.True(Screenshots.Save(window, "channels-component-rgb-preview"));
            session.Undo(); Assert.Same(original, session.ActiveLayer.Pixels);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task New_alpha_dialog_brush_and_delete_do_not_change_the_selected_image_layer()
    {
        var window = new MainWindow { Width = 1100, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 240, SKColors.CornflowerBlue); window.AddSession(session); var original = session.ActiveLayer!.Pixels!; var before = original.Bytes;
            OpenChannels(window); var panel = window.GetVisualDescendants().OfType<ChannelsPanel>().Single();
            panel.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "New alpha channel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); var dialog = Assert.Single(window.OwnedWindows);
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Product edge"; dialog.Close(true);
            for (var i = 0; i < 20 && !session.IsEditingAlphaChannel; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal("Product edge", session.EditingAlphaChannel);
            window.SelectTool(Tool.Brush); session.Foreground = SKColors.White; session.Brush = session.Brush with { Size = 30, Hardness = 1 };
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            window.MouseDown(At(100, 80), MouseButton.Left); window.MouseMove(At(180, 80), RawInputModifiers.LeftMouseButton); window.MouseUp(At(180, 80), MouseButton.Left);
            Assert.Equal(255, session.Document.AlphaChannels["Product edge"].GetPixel(140, 80).Alpha); Assert.Same(original, session.ActiveLayer.Pixels); Assert.Equal(before, original.Bytes);
            session.SetChannelView(RgbChannels.All, alphaOverlay: true); Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(window, "channels-alpha-overlay"));
            window.Canvas.Focus(); window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.Single(session.Document.Layers); Assert.Equal(0, session.Document.AlphaChannels["Product edge"].GetPixel(140, 80).Alpha);
            session.Undo(); Assert.Equal(255, session.Document.AlphaChannels["Product edge"].GetPixel(140, 80).Alpha);
        }
        finally { window.Close(); }
    }

    private static void OpenChannels(MainWindow window)
    {
        var menu = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => m.Header as string == "_Window");
        menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Channels").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow window, Control control, Point at)
    {
        var point = control.TranslatePoint(at, window)!.Value; window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
}

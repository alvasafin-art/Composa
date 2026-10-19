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
using SkiaSharp;

namespace Composa.App.Tests;

public class ChannelUiTests
{
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

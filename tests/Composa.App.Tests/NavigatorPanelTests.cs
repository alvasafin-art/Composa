using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class NavigatorPanelTests
{
    [AvaloniaFact]
    public void Zoom_drag_is_logarithmic_Alt_is_fine_and_the_numeric_editor_keeps_printable_keys()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var session = Scene(); window.AddSession(session); Toggle(window);
        try
        {
            var panel = Panel(window); var field = panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Name == "NavigatorZoom"); window.Canvas.ZoomTo(1); Pump(); var state = session.History.CurrentId;
            var at = field.TranslatePoint(new Point(50, 12), window)!.Value;
            window.MouseDown(at, MouseButton.Left); window.MouseMove(at + new Vector(12, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(at + new Vector(12, 0), MouseButton.Left); Pump();
            var percent = Math.Round(100 * Math.Pow(6400, 12 / field.Bounds.Width), 1); Assert.InRange(Math.Abs(window.Canvas.Zoom * 100 - percent), 0, .001);
            window.MouseDown(at, MouseButton.Left); window.MouseMove(at + new Vector(-12, 0), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt); window.MouseUp(at + new Vector(-12, 0), MouseButton.Left, RawInputModifiers.Alt); Pump();
            var fine = Math.Round(percent * Math.Pow(6400, -12 / field.Bounds.Width / 10), 1); Assert.InRange(Math.Abs(window.Canvas.Zoom * 100 - fine), 0, .001);
            field.BeginEdit(); Pump(); var input = field.GetVisualDescendants().OfType<TextBox>().Single(); var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D9 }; input.RaiseEvent(key); Assert.False(key.Handled);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(); Assert.Equal(state, session.History.CurrentId);
        }
        finally { session.MarkSaved("navigator.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Navigator_pixels_convert_from_working_P3_to_display_RGB_once_without_changing_source_or_export()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var color = new SKColor(100, 150, 200); var session = EditorSession.NewCanvas(300, 200, color, ColorDepth.Sixteen); session.AssignColorProfile(RgbProfile.DisplayP3); window.AddSession(session); Toggle(window);
        var profilePath = Path.Combine(Path.GetTempPath(), "navigator-" + Guid.NewGuid().ToString("N") + ".icc");
        try
        {
            var source = session.ActiveLayer!.Pixels!; var bytes = source.Bytes; using var before = session.Flatten(); var state = session.History.CurrentId; var panel = Panel(window);
            await UseDisplayProfile(window, profilePath); panel.RefreshThumbnail(); Assert.True(Screenshots.Save(window, "navigator-color-managed")); Assert.Equal(RgbProfile.Rgb1998, window.DisplayProfile);
            var point = Preview(panel).TranslatePoint(panel.ImageBounds.Center, window)!.Value; using var shot = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "navigator-color-managed.png")); var actual = shot.GetPixel((int)point.X, (int)point.Y); var expected = RgbProfile.DisplayP3.ConvertColor(color, RgbProfile.Rgb1998);
            Assert.InRange(Math.Abs(actual.Red - expected.Red), 0, 2); Assert.InRange(Math.Abs(actual.Green - expected.Green), 0, 2); Assert.InRange(Math.Abs(actual.Blue - expected.Blue), 0, 2);
            using var after = session.Flatten(); Assert.Equal(before.Bytes, after.Bytes); Assert.Equal(bytes, source.Bytes); Assert.Equal(state, session.History.CurrentId);
        }
        finally { session.MarkSaved("navigator.cmps", session.History.CurrentId); window.Close(); File.Delete(profilePath); }
    }

    [AvaloniaFact]
    public void Real_pointer_drag_preserves_the_grab_offset_and_clamps_to_the_image_without_history_or_thumbnail_work()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var session = Scene(); window.AddSession(session); Toggle(window);
        try
        {
            Pump(); var panel = Panel(window); panel.RefreshThumbnail(); var view = window.Canvas; view.ZoomTo(2); view.CenterOn(new(600, 400)); Pump();
            var state = session.History.CurrentId; var pixels = session.ActiveLayer!.Pixels!; var bytes = pixels.Bytes; var builds = panel.ThumbnailBuilds;
            var target = Preview(panel); var box = panel.ImageBounds; var center = view.ViewportCenter;
            var at = target.TranslatePoint(panel.ViewFrame.Center + new Vector(4, 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left); Assert.InRange(Math.Abs(view.ViewportCenter.X - center.X), 0, .001);
            window.MouseMove(at + new Vector(20, 6), RawInputModifiers.LeftMouseButton); window.MouseUp(at + new Vector(20, 6), MouseButton.Left); Pump();
            Assert.InRange(Math.Abs(view.ViewportCenter.X - center.X - 20 * session.Document.Width / box.Width), 0, .01);
            Assert.InRange(Math.Abs(view.ViewportCenter.Y - center.Y - 6 * session.Document.Height / box.Height), 0, .01);
            Assert.Equal(state, session.History.CurrentId); Assert.Equal(bytes, pixels.Bytes); panel.RefreshThumbnail(); Assert.Equal(builds, panel.ThumbnailBuilds);
            var edge = target.TranslatePoint(new Point(box.Right - 1, box.Bottom - 1), window)!.Value; window.MouseDown(edge, MouseButton.Left); window.MouseMove(edge + new Vector(500, 500), RawInputModifiers.LeftMouseButton); window.MouseUp(edge + new Vector(500, 500), MouseButton.Left); Pump();
            Assert.InRange(Math.Abs(view.VisibleDocumentRect.Right - session.Document.Width), 0, .01); Assert.InRange(Math.Abs(view.VisibleDocumentRect.Bottom - session.Document.Height), 0, .01);
            var frame = panel.ViewFrame; Assert.True(frame.Right <= box.Right + .01 && frame.Bottom <= box.Bottom + .01);
            Assert.True(Screenshots.Save(window, "navigator-zoomed-rgb16"));
        }
        finally { session.MarkSaved("navigator.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Zoom_buttons_number_wheel_keyboard_and_rulers_track_the_same_view_without_dirtying_the_document()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var session = Scene(); window.AddSession(session); Toggle(window);
        try
        {
            Pump(); var panel = Panel(window); var view = window.Canvas; var state = session.History.CurrentId;
            ClickButton(panel, "NavigatorActual"); Assert.Equal(1, view.Zoom);
            var field = panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Name == "NavigatorZoom"); field.BeginEdit(); Pump(); var input = field.GetVisualDescendants().OfType<TextBox>().Single(); input.Text = "400"; window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Pump(); Assert.Equal(4, view.Zoom);
            var preview = Preview(panel); var at = preview.TranslatePoint(panel.ImageBounds.Center, window)!.Value;
            window.MouseWheel(at, new Vector(0, -1), RawInputModifiers.None); Assert.Equal(3, view.Zoom); Assert.Equal(300, field.Value);
            view.CenterOn(new(600, 400)); var old = view.ViewportCenter; preview.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Pump(); Assert.True(view.ViewportCenter.X > old.X);
            session.View = session.View with { ShowRulers = true }; view.ViewOptionsChanged(false); Pump(); var a = view.ToDocument(new Point(CanvasView.RulerThickness, CanvasView.RulerThickness)); var rect = view.VisibleDocumentRect; Assert.InRange(Math.Abs(rect.Left - a.X), 0, .001); Assert.InRange(Math.Abs(rect.Top - a.Y), 0, .001);
            ClickButton(panel, "NavigatorFit"); Pump(); Assert.InRange(Math.Abs(view.VisibleDocumentRect.Width - session.Document.Width), 0, .001); Assert.InRange(Math.Abs(panel.ViewFrame.Width - panel.ImageBounds.Width), 0, .001);
            panel.RefreshThumbnail(); Assert.Equal(state, session.History.CurrentId); Assert.True(Screenshots.Save(window, "navigator-fitted-rulers"));
        }
        finally { session.MarkSaved("navigator.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Hidden_collapsed_and_switched_documents_refresh_only_when_needed_and_follow_the_display_profile()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); var first = Scene(); window.AddSession(first); Toggle(window);
        var profilePath = Path.Combine(Path.GetTempPath(), "navigator-" + Guid.NewGuid().ToString("N") + ".icc");
        try
        {
            Pump(); var panel = Panel(window); panel.RefreshThumbnail(); var count = panel.ThumbnailBuilds; Assert.True(count > 0);
            first.SetVisible(first.ActiveLayer!, false); first.SetVisible(first.ActiveLayer!, true); panel.RefreshThumbnail(); Assert.Equal(count + 1, panel.ThumbnailBuilds);
            Toggle(window); Pump(); Assert.False(window.Settings.Dock["Navigator"].Visible); first.SetVisible(first.ActiveLayer!, false); panel.RefreshThumbnail(); Assert.Equal(count + 1, panel.ThumbnailBuilds);
            Toggle(window); Pump(); panel.RefreshThumbnail(); Assert.Equal(count + 2, panel.ThumbnailBuilds);
            var header = window.GetVisualDescendants().OfType<Border>().Single(b => ToolTip.GetTip(b) is "Collapse Navigator"); var at = header.TranslatePoint(new Point(20, 10), window)!.Value; window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left); Pump(); Assert.True(window.Settings.Dock["Navigator"].Collapsed);
            var collapsed = panel.ThumbnailBuilds; first.SetVisible(first.ActiveLayer!, true); panel.RefreshThumbnail(); Assert.Equal(collapsed, panel.ThumbnailBuilds);
            header = window.GetVisualDescendants().OfType<Border>().Single(b => ToolTip.GetTip(b) is "Expand Navigator"); at = header.TranslatePoint(new Point(20, 10), window)!.Value; window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left); Pump(); panel.RefreshThumbnail();
            var second = EditorSession.NewCanvas(300, 300, SKColors.OrangeRed); window.AddSession(second); Pump(); panel.RefreshThumbnail(); Assert.Same(second, panel.Session); Assert.InRange(Math.Abs(panel.ImageBounds.Width - panel.ImageBounds.Height), 0, .001);
            await UseDisplayProfile(window, profilePath); panel.RefreshThumbnail(); Assert.Equal(RgbProfile.Rgb1998, panel.DisplayProfile);
            typeof(MainWindow).GetMethod("SetSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [first]); Pump(); panel.RefreshThumbnail(); Assert.Same(first, panel.Session); Assert.Equal(RgbProfile.DisplayP3, first.Document.Profile);
            Assert.True(Screenshots.Save(window, "navigator-display-profile"));
        }
        finally { foreach (var session in window.Sessions) session.MarkSaved("navigator.cmps", session.History.CurrentId); window.Close(); File.Delete(profilePath); }
    }

    private static async Task UseDisplayProfile(MainWindow window, string path)
    {
        await File.WriteAllBytesAsync(path, RgbProfile.Rgb1998.IccBytes(), TestContext.Current.CancellationToken);
        window.Settings.DisplayColorMode = DisplayColorMode.Custom; window.Settings.DisplayProfilePath = path;
        await window.RefreshDisplayColor();
    }

    private static EditorSession Scene()
    {
        var d = new Document(1200, 800) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; var image = Pixels.NewWorkingColor(d.Width, d.Height, d.Depth, d.Profile); var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        for (var y = 0; y < d.Height; y++) for (var x = 0; x < d.Width; x++) { var at = (y * d.Width + x) * 4; pixels[at] = (x / 100 + y / 100) % 2 == 0 ? .2f : .7f; pixels[at + 1] = y / 1100f; pixels[at + 2] = x / 1500f; pixels[at + 3] = 1; }
        d.InsertAboveActive(Layer.Raster("RGB16 texture", image)); return new(d);
    }
    private static void Toggle(MainWindow window) { window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "Navigator").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(); }
    private static NavigatorPanel Panel(MainWindow window) => window.GetVisualDescendants().OfType<NavigatorPanel>().Single();
    private static Control Preview(NavigatorPanel panel) => panel.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "NavigatorPreview");
    private static void ClickButton(NavigatorPanel panel, string name) { panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}

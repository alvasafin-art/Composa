using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class DisplayColorTests
{
    [AvaloniaFact]
    public async Task Untagged_reference_previews_are_assumed_sRGB_on_a_profiled_display()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-display-" + Guid.NewGuid().ToString("N") + ".icc"); File.WriteAllBytes(path, RgbProfile.DisplayP3.IccBytes());
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show(); window.AiTasks.SetConnectedForTests();
        try
        {
            window.AddSession(EditorSession.NewCanvas(320, 200));
            window.Settings.DisplayColorMode = DisplayColorMode.Custom; window.Settings.DisplayProfilePath = path; await window.RefreshDisplayColor();
            var color = new SKColor(100, 150, 200); var reference = new SKBitmap(32, 32); reference.Erase(color); window.AddAiReferenceForTests(reference);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b)?.ToString() == "Preview reference: Test reference");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Single(); Assert.True(Screenshots.Save(dialog, "display-untagged-reference"));
            var view = dialog.GetVisualDescendants().OfType<Image>().Single(); var point = view.TranslatePoint(new Rect(view.Bounds.Size).Center, dialog)!.Value;
            using var shot = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "display-untagged-reference.png"));
            var actual = shot.GetPixel((int)point.X, (int)point.Y); var expected = RgbProfile.Srgb.ConvertColor(color, RgbProfile.DisplayP3);
            Assert.InRange(Math.Abs(actual.Red - expected.Red), 0, 1); Assert.InRange(Math.Abs(actual.Green - expected.Green), 0, 1); Assert.InRange(Math.Abs(actual.Blue - expected.Blue), 0, 1);
            dialog.Close();
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public void Long_custom_profile_names_do_not_expand_the_status_or_profile_dialog()
    {
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 200);
            session.AssignColorProfile(RgbProfile.FromIcc(RgbProfile.DisplayP3.IccBytes(), new string('W', 200))); window.AddSession(session);
            var image = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => m.Header as string == "_Image");
            image.Items.OfType<MenuItem>().Single(m => m.Header as string == "Assign Color Profile…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Single(); Assert.True(Screenshots.Save(dialog, "long-rgb-profile-name"));
            Assert.InRange(dialog.Bounds.Width, 350, 500); dialog.Close(false);
            var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("72 ppi") == true);
            Assert.True(status.Bounds.Width <= 430);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Canvas_targets_display_RGB_once_without_clipping_through_sRGB_or_changing_export()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-display-" + Guid.NewGuid().ToString("N") + ".icc");
        File.WriteAllBytes(path, RgbProfile.DisplayP3.IccBytes());
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 200, new SKColor(100, 150, 200), ColorDepth.Sixteen);
            session.AssignColorProfile(RgbProfile.DisplayP3); session.AddShape(new ShapeStyle(ShapeKind.Rectangle, 0xFF00FF00, 0), new(160, 0, 320, 200));
            window.AddSession(session); window.SelectTool(Tool.Hand);
            var canvas = window.GetVisualDescendants().OfType<CanvasView>().Single(); canvas.ShowTransformControls = false;
            using var original = session.Flatten(); var state = session.History.CurrentId;
            window.Settings.DisplayColorMode = DisplayColorMode.Custom; window.Settings.DisplayProfilePath = path;
            await window.RefreshDisplayColor();
            Assert.Equal(RgbProfile.DisplayP3, window.DisplayProfile);
            Assert.True(Screenshots.Save(window, "display-p3-canvas"));
            SKColor At(string name, float x)
            {
                using var shot = SKBitmap.Decode(Path.Combine(Screenshots.Folder, name + ".png"));
                var point = canvas.TranslatePoint(canvas.ToScreen(new(x, 100)), window)!.Value;
                return shot.GetPixel((int)Math.Round(point.X), (int)Math.Round(point.Y));
            }
            Assert.Equal(new SKColor(100, 150, 200), At("display-p3-canvas", 80));
            Assert.Equal(SKColors.Lime, At("display-p3-canvas", 240));
            window.Settings.DisplayColorMode = DisplayColorMode.Srgb; await window.RefreshDisplayColor();
            Assert.True(Screenshots.Save(window, "display-srgb-canvas"));
            var expected = RgbProfile.DisplayP3.ConvertColor(new(100, 150, 200), RgbProfile.Srgb); var actual = At("display-srgb-canvas", 80);
            Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 1); Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 1); Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 1);
            using var after = session.Flatten(); Assert.Equal(original.Bytes, after.Bytes);
            Assert.Equal(state, session.History.CurrentId); Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(after));
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_custom_display_profile_also_corrects_an_sRGB_pickers_spectrum_and_preview()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-display-" + Guid.NewGuid().ToString("N") + ".icc"); File.WriteAllBytes(path, RgbProfile.Rgb1998.IccBytes());
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 200); window.AddSession(session);
            window.Settings.DisplayColorMode = DisplayColorMode.Custom; window.Settings.DisplayProfilePath = path; await window.RefreshDisplayColor();
            var initial = new SKColor(100, 150, 200);
            var pick = Prompts.Color(window, "sRGB on a calibrated display", initial); Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Single(); var picker = dialog.GetVisualDescendants().OfType<WorkingColorPicker>().Single();
            Assert.Equal(initial, picker.Color); Assert.True(Screenshots.Save(dialog, "display-profile-color-picker"));
            dialog.Close(true); Assert.Equal(initial, await pick);
            var view = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => m.Header as string == "_View");
            view.Items.OfType<MenuItem>().Single(m => m.Header as string == "Display Color…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.Single(); Assert.True(Screenshots.Save(dialog, "display-color-settings")); dialog.Close(false);
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_missing_or_invalid_display_ICC_falls_back_without_affecting_the_document()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var session = EditorSession.NewCanvas(32, 24, SKColors.Red); window.AddSession(session); var state = session.History.CurrentId;
            window.Settings.DisplayColorMode = DisplayColorMode.Custom; window.Settings.DisplayProfilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".icc");
            await window.RefreshDisplayColor(); Assert.Equal(RgbProfile.Srgb, window.DisplayProfile); Assert.Equal(state, session.History.CurrentId);
        }
        finally { window.Close(); }
    }
}

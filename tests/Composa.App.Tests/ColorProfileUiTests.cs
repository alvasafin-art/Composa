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

public class ColorProfileUiTests
{
    [AvaloniaFact]
    public async Task Open_keeps_the_images_profile_instead_of_clipping_it_to_sRGB()
    {
        using var image = Pixels.NewWorkingColor(32, 24, ColorDepth.Sixteen, RgbProfile.DisplayP3);
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        for (var i = 0; i < 32 * 24; i++) { values[i * 4 + 1] = 1; values[i * 4 + 3] = 1; }
        var path = Path.Combine(Path.GetTempPath(), "composa-wide-" + Guid.NewGuid().ToString("N") + ".png");
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        try
        {
            Composa.IO.ImageFiles.Save(image, path, Composa.IO.ExportFormat.Png);
            var session = await window.OpenPath(path);
            Assert.Equal(RgbProfile.DisplayP3, session!.Document.Profile); Assert.Equal(SKColors.Lime, session.SampleColor(16, 12));
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public void Profile_menu_is_undoable_and_conversion_explains_layer_appearance()
    {
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(320, 200, new SKColor(100, 150, 200), ColorDepth.Sixteen); window.AddSession(session);
        void Command(string name)
        {
            var image = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => m.Header as string == "_Image");
            image.Items.OfType<MenuItem>().Single(m => m.Header as string == name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        Command("Assign Color Profile…");
        var dialog = window.OwnedWindows.Single();
        dialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
        Assert.True(Screenshots.Save(dialog, "assign-rgb-profile"));
        dialog.Close(true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(RgbProfile.Rgb1998, session.Document.Profile);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("RGB (1998)") == true);
        session.Undo(); Assert.Equal(RgbProfile.Srgb, session.Document.Profile); session.Redo();
        Command("Convert Color Profile…"); dialog = window.OwnedWindows.Single();
        Assert.True(dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked);
        Assert.True(Screenshots.Save(dialog, "convert-rgb-profile"));
        dialog.Close(false); window.Close();
    }

    [AvaloniaFact]
    public void Wide_gamut_picker_keeps_document_numbers_and_corrects_spectrum_and_swatches_for_display()
    {
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(320, 200, null, ColorDepth.Sixteen); window.AddSession(session);
        session.AssignColorProfile(RgbProfile.Rgb1998);
        var initial = new SKColor(100, 150, 200);
        var pick = Prompts.Color(window, "Working RGB color", initial); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var picker = dialog.GetVisualDescendants().OfType<WorkingColorPicker>().Single();
        Assert.Equal(initial, picker.Color);
        Assert.True(Screenshots.Save(dialog, "working-rgb-color-picker"));
        dialog.Close(true); Dispatcher.UIThread.RunJobs(); Assert.Equal(initial, pick.Result);
        // The eyedropper must read working RGB directly; an sRGB preview can clip saturated colors.
        var layer = session.ActiveLayer!;
        session.Fill(SKColors.Lime);
        Assert.Equal(SKColors.Lime, session.SampleColor(100, 100));
        Assert.Equal(RgbProfile.Rgb1998, Pixels.ProfileOf(layer.Pixels!));
        window.Close();
    }
}

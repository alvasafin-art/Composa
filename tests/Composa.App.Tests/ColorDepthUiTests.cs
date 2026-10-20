using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class ColorDepthUiTests
{
    [AvaloniaFact]
    public void New_canvas_selects_precision_and_refuses_an_oversized_float_canvas()
    {
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        var request = CanvasDialogs.NewCanvas(window, SKColors.White); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var precision = dialog.GetVisualDescendants().OfType<ComboBox>().Last();
        precision.SelectedIndex = 1;
        var size = dialog.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        size[0].Value = 8000; size[1].Value = 8000;
        var create = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Create");
        Assert.False(create.IsEnabled);
        size[0].Value = 1200; size[1].Value = 800;
        Assert.True(create.IsEnabled);
        Assert.True(Screenshots.Save(dialog, "new-canvas-rgb16"));
        dialog.Close(true); Dispatcher.UIThread.RunJobs();
        Assert.Equal(ColorDepth.Sixteen, request.Result!.Depth);
        window.Close();
    }

    [AvaloniaFact]
    public void Precision_menu_converts_and_undo_restores_the_document_and_status()
    {
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        var session = EditorSession.NewCanvas(320, 200, SKColors.CornflowerBlue, ColorDepth.Sixteen); window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("16-bit") == true);
        var image = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => m.Header as string == "_Image");
        var submenu = image.Items.OfType<MenuItem>().Single(m => m.Header as string == "RGB Precision");
        submenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "8 Bits/Channel").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(ColorDepth.Eight, session.Document.Depth);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("8-bit") == true);
        session.Undo(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(ColorDepth.Sixteen, session.Document.Depth);
        Assert.True(Screenshots.Save(window, "rgb16-document"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Normal_open_keeps_a_16bit_PNG_document_and_its_export_precision()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "rgb16-window-" + Guid.NewGuid().ToString("N") + ".png");
        var window = new MainWindow { Width = 1100, Height = 760 }; window.Show();
        try
        {
            using var image = Pixels.NewColor(512, 16, ColorDepth.Sixteen);
            var data = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
            for (var i = 0; i < 512 * 16; i++) { data[i * 4] = (24000 + i % 512) / 65535f; data[i * 4 + 3] = 1; }
            ImageFiles.Save(image, path, ExportFormat.Png);
            var session = await window.OpenPath(path);
            Assert.NotNull(session); Assert.Equal(ColorDepth.Sixteen, session.Document.Depth);
            using var flat = session.Flatten(); Assert.Equal(SKColorType.RgbaF32, flat.ColorType);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(flat.GetPixelSpan());
            var levels = new HashSet<float>(); for (var x = 0; x < 512; x++) levels.Add(pixels[x * 4]);
            Assert.Equal(512, levels.Count);
        }
        finally { window.Close(); File.Delete(path); }
    }
}

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.App.Mcp;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using Magick = ImageMagick;
using ModelContextProtocol;

namespace Composa.App.Tests;

public class ColorLookupUiTests
{
    [AvaloniaTheory]
    [InlineData("tab")] [InlineData("layer")] [InlineData("edit")] [InlineData("cancel")]
    public async Task Slow_lookup_load_never_edits_a_changed_target_or_applies_a_cancelled_result(string change)
    {
        var window = new MainWindow(); window.Show(); var session = EditorSession.NewCanvas(40, 30); session.AddBlankLayer(); window.AddSession(session);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var result = new TaskCompletionSource<CubeLut>(TaskCreationOptions.RunContinuationsAsynchronously); using var cancellation = new CancellationTokenSource();
        try
        {
            var task = new ComposaTools(window).LoadColorLookup(() => { started.SetResult(); return result.Task; }, 50, LookupRgbSpace.Srgb, true, null, null, cancellation.Token);
            await Until(() => started.Task.IsCompleted);
            if (change == "tab") window.AddSession(EditorSession.NewCanvas(40, 30));
            else if (change == "layer") session.SelectLayer(session.Document.Layers[0].Id);
            else if (change == "edit") session.AddBlankLayer();
            else cancellation.Cancel();
            var states = window.Sessions.Select(s => (s.History.CurrentId, s.Document.Layers.Count)).ToArray();
            result.SetResult(CubeLut.Read(new StringReader("LUT_1D_SIZE 2\n1 1 1\n0 0 0\n"))); await Until(() => task.IsCompleted);
            if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            else await Assert.ThrowsAsync<McpException>(() => task);
            Assert.Equal(states, window.Sessions.Select(s => (s.History.CurrentId, s.Document.Layers.Count)).ToArray());
            Assert.DoesNotContain(window.Sessions.SelectMany(s => s.Document.AllLayers()), l => l.Adjustment is ColorLookupAdjustment);
        }
        finally { foreach (var s in window.Sessions) s.MarkSaved("lut-target.cmps", s.History.CurrentId); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Lookup_embedded_table_profile_strength_preview_and_cancel_preserve_one_undo_step()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show();
        var document = new Document(400, 240) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; var source = Pixels.NewWorkingColor(400, 240, document.Depth, document.Profile); var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        for (var y = 0; y < 240; y++) for (var x = 0; x < 400; x++) { var at = (y * 400 + x) * 4; pixels[at] = x / 500f; pixels[at + 1] = y / 300f; pixels[at + 2] = .25f; pixels[at + 3] = 1; }
        document.InsertAboveActive(Layer.Raster("Gradient", source)); var session = new EditorSession(document); var original = new ColorLookupAdjustment { Table = CubeLut.Read(new StringReader("TITLE \"Warm lift\"\nLUT_1D_SIZE 2\n.05 0 0\n1 1 .8\n")) };
        var layer = session.AddAdjustmentLayer(original); window.AddSession(session); var state = session.History.CurrentId;
        try
        {
            var task = Edit(window, layer); var dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Warm lift", dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LookupTitle").Text);
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LookupSpace").SelectedIndex = Array.IndexOf(Enum.GetValues<LookupRgbSpace>(), LookupRgbSpace.WorkingRgb);
            var field = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Name == "LookupStrength"); field.BeginEdit(); Dispatcher.UIThread.RunJobs(); field.GetVisualDescendants().OfType<TextBox>().Single().Text = "40"; dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Tick(); Assert.Equal(40, Assert.IsType<ColorLookupAdjustment>(layer.Adjustment).Strength); Assert.Equal(LookupRgbSpace.WorkingRgb, ((ColorLookupAdjustment)layer.Adjustment!).Space); Assert.Equal(state, session.History.CurrentId);
            var preview = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Preview")); preview.IsChecked = false; await Tick(); Assert.True(layer.Adjustment!.IsIdentity); preview.IsChecked = true; await Tick(); Assert.Equal(40, ((ColorLookupAdjustment)layer.Adjustment!).Strength);
            Assert.True(Screenshots.Save(dialog, "color-lookup-rgb16")); dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Finish(task); Assert.Equal("Edit Adjustment", session.History.UndoName);
            task = Edit(window, layer); dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ClearLookup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Tick(); Assert.Null(((ColorLookupAdjustment)layer.Adjustment!).Table);
            dialog.Close(false); await Finish(task); layer = session.ActiveLayer!; Assert.Same(original.Table, ((ColorLookupAdjustment)layer.Adjustment!).Table); Assert.Equal(40, ((ColorLookupAdjustment)layer.Adjustment!).Strength);
            session.Undo(); Assert.Equal(state, session.History.CurrentId); Assert.True(original.ContentEquals(session.ActiveLayer!.Adjustment!)); Assert.Same(source, session.Document.Layers[0].Pixels);
        }
        finally { session.MarkSaved("lookup-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    [Fact]
    public void sRGB_lookup_in_P3_agrees_with_independent_LittleCMS_Q16_before_and_after_the_table()
    {
        using var original = Pixels.NewWorkingColor(32, 1, ColorDepth.Sixteen, RgbProfile.DisplayP3);
        var input = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(original.GetPixelSpan()); var raw = new byte[32 * 8];
        for (var x = 0; x < 32; x++) for (var c = 0; c < 4; c++)
        {
            var v = (ushort)(c == 3 ? 40000 : c == 0 ? 12000 + x * 650 : c == 1 ? 25000 : 38000 - x * 400);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan((x * 4 + c) * 2), v); input[x * 4 + c] = v / 65535f * (c == 3 ? 1 : 40000 / 65535f);
        }
        using var first = new Magick.MagickImage(); first.Read(raw, new Magick.MagickReadSettings { Width = 32, Height = 1, Depth = 16, Endian = Magick.Endian.MSB, Format = Magick.MagickFormat.Rgba });
        first.SetProfile(new Magick.ColorProfile(RgbProfile.DisplayP3.IccBytes())); first.RenderingIntent = Magick.RenderingIntent.Relative; first.TransformColorSpace(new Magick.ColorProfile(RgbProfile.Srgb.IccBytes()), Magick.ColorTransformMode.HighRes);
        using (var data = first.GetPixels()) for (var x = 0; x < 32; x++)
        {
            var color = data.GetPixel(x, 0).ToArray();
            double[] adjusted = [.1 + .8 * color[2] / 65535.0, .05 + .85 * color[1] / 65535.0, .05 + .8 * color[0] / 65535.0, color[3] / 65535.0];
            for (var c = 0; c < 4; c++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan((x * 4 + c) * 2), (ushort)Math.Round(adjusted[c] * 65535));
        }
        using var reference = new Magick.MagickImage(); reference.Read(raw, new Magick.MagickReadSettings { Width = 32, Height = 1, Depth = 16, Endian = Magick.Endian.MSB, Format = Magick.MagickFormat.Rgba });
        reference.SetProfile(new Magick.ColorProfile(RgbProfile.Srgb.IccBytes())); reference.RenderingIntent = Magick.RenderingIntent.Relative; reference.TransformColorSpace(new Magick.ColorProfile(RgbProfile.DisplayP3.IccBytes()), Magick.ColorTransformMode.HighRes);
        var text = new System.Text.StringBuilder("LUT_3D_SIZE 2\n");
        for (var b = 0; b < 2; b++) for (var g = 0; g < 2; g++) for (var r = 0; r < 2; r++) text.AppendLine(FormattableString.Invariant($"{.1 + .8 * b:R} {.05 + .85 * g:R} {.05 + .8 * r:R}"));
        new ColorLookupAdjustment { Table = CubeLut.Read(new StringReader(text.ToString())) }.Apply(original);
        using var expected = reference.GetPixels(); var actual = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(original.GetPixelSpan()); double max = 0;
        for (var x = 0; x < 32; x++)
        { var color = expected.GetPixel(x, 0).ToArray(); Assert.Equal(40000 / 65535f, actual[x * 4 + 3]); for (var c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(actual[x * 4 + c] / actual[x * 4 + 3] - color[c] / 65535.0)); }
        Assert.True(max <= .0002, $"Maximum difference from independent LittleCMS Q16: {max:R}");
    }
    private static Task Edit(MainWindow window, Layer layer) => (Task)typeof(MainWindow).GetMethod("EditAdjustmentLayer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [layer, false])!;
    private static async Task Tick() { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
    private static async Task Finish(Task task) { for (var i = 0; i < 300 && !task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); } Assert.True(task.IsCompleted); await task; }
    private static async Task Until(Func<bool> ready) { for (var i = 0; i < 300 && !ready(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); } Assert.True(ready()); }
}

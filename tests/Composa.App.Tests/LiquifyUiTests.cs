using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.IO;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class LiquifyUiTests
{
    public static bool HasPhotoCorpus => Environment.GetEnvironmentVariable("COMPOSA_MATTING_CORPUS_DIR") is { } path && File.Exists(Path.Combine(path, "input", "GT25.png"));

    [AvaloniaFact(SkipUnless = nameof(HasPhotoCorpus), Skip = "Requires COMPOSA_MATTING_CORPUS_DIR with the published Alpha Matting corpus")]
    public async Task Photographic_fine_mesh_full_resolution_preview_accept_save_and_undo_retain_RGB16_P3()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("COMPOSA_MATTING_CORPUS_DIR")!, "input", "GT25.png");
        using var decoded = SKBitmap.Decode(path); using var srgb = Pixels.DisplayCopy(decoded);
        var image = Pixels.ConvertWorking(srgb, ColorDepth.Sixteen, RgbProfile.DisplayP3); var before = image.Bytes;
        var document = new Document(image.Width, image.Height) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 }; document.InsertAboveActive(Layer.Raster("Fine mesh photograph", image));
        var session = new EditorSession(document); var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); window.AddSession(session);
        try
        {
            var task = Invoke(window); var dialog = window.OwnedWindows.OfType<LiquifyDialog>().Single(); await Preview(dialog); Size(dialog, 650);
            Drag(dialog, new(image.Width * .5f, image.Height * .55f), new(image.Width * .54f, image.Height * .55f));
            var full = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "100%"); full.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Preview(dialog);
            Assert.StartsWith($"{image.Width}×{image.Height}", dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LiquifyStatus").Text);
            Tool(dialog, LiquifyTool.Freeze); Size(dialog, 200); Drag(dialog, new(image.Width * .5f, image.Height * .5f), new(image.Width * .5f, image.Height * .5f));
            await Preview(dialog); Assert.True(Screenshots.Save(dialog, "liquify-real-net-rgb16")); Accept(dialog); await Finish(task);
            Assert.Equal(before, image.Bytes); Assert.NotSame(image, session.ActiveLayer!.Pixels); Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer.Pixels!.ColorType);
            using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0; var loaded = ProjectFile.Read(stream);
            Assert.Equal(RgbProfile.DisplayP3, loaded.Profile); Assert.Equal(SKColorType.RgbaF32, loaded.ActiveLayer!.Pixels!.ColorType);
            // Native RGB16 serialization is rounded to 16-bit channel levels, rather than retaining arbitrary float bits.
            foreach (var (x, y) in new[] { (0, 0), (image.Width / 2, image.Height / 2), (image.Width * 54 / 100, image.Height * 55 / 100), (image.Width - 1, image.Height - 1) })
            {
                var expected = new float[4]; var actual = new float[4];
                System.Runtime.InteropServices.Marshal.Copy(session.ActiveLayer.Pixels.GetPixels() + y * session.ActiveLayer.Pixels.RowBytes + x * 16, expected, 0, 4);
                System.Runtime.InteropServices.Marshal.Copy(loaded.ActiveLayer.Pixels.GetPixels() + y * loaded.ActiveLayer.Pixels.RowBytes + x * 16, actual, 0, 4);
                for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(expected[c] - actual[c]), 0, 2f / 65535);
            }
            session.Undo(); Assert.Same(image, session.ActiveLayer!.Pixels);
        }
        finally { session.MarkSaved("liquify-photo.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Pointer_freeze_thaw_reconstruct_and_local_history_apply_as_one_RGB16_edit()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); var session = Scene(); window.AddSession(session);
        var source = session.ActiveLayer!.Pixels!; var original = source.Bytes; var state = session.History.CurrentId;
        try
        {
            session.SmearMode = SmearMode.Liquify; window.SelectTool(Composa.Editing.Tool.Smear); Dispatcher.UIThread.RunJobs();
            Assert.True(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LiquifyWorkspace").IsEnabled);
            var task = Invoke(window); var dialog = window.OwnedWindows.OfType<LiquifyDialog>().Single(); await Preview(dialog);
            FullBrush(dialog); Size(dialog, 90);
            Drag(dialog, new(160.5f, 150.5f), new(190.5f, 150.5f)); var displaced = dialog.Field.DisplacementAt(190, 150); Assert.True(displaced.X < -25);
            Assert.Equal(state, session.History.CurrentId); Assert.Same(source, session.ActiveLayer.Pixels);
            Tool(dialog, LiquifyTool.Freeze); Size(dialog, 50); Drag(dialog, new(190.5f, 150.5f), new(190.5f, 150.5f)); Assert.Equal(255, dialog.Field.FrozenAt(190, 150));
            Tool(dialog, LiquifyTool.Reconstruct); Drag(dialog, new(190.5f, 150.5f), new(190.5f, 150.5f)); Assert.Equal(displaced, dialog.Field.DisplacementAt(190, 150));
            await Preview(dialog); Assert.True(Screenshots.Save(dialog, "liquify-freeze-rgb16"));
            Tool(dialog, LiquifyTool.Freeze); Drag(dialog, new(190.5f, 150.5f), new(190.5f, 150.5f), RawInputModifiers.Alt); Assert.Equal(0, dialog.Field.FrozenAt(190, 150));
            Tool(dialog, LiquifyTool.Reconstruct); Drag(dialog, new(190.5f, 150.5f), new(190.5f, 150.5f)); Assert.Equal(default, dialog.Field.DisplacementAt(190, 150));
            dialog.View.Focus(); dialog.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); Assert.Equal(displaced, dialog.Field.DisplacementAt(190, 150));
            dialog.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift); Assert.Equal(default, dialog.Field.DisplacementAt(190, 150));
            Assert.Equal(original, source.Bytes); Accept(dialog); await Finish(task);
            Assert.NotSame(source, session.ActiveLayer.Pixels); Assert.Equal("Liquify", session.History.UndoName); Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(session.ActiveLayer.Pixels!));
            Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(session.ActiveLayer.Pixels!)); session.Undo(); Assert.Equal(state, session.History.CurrentId); Assert.Same(source, session.ActiveLayer!.Pixels);
        }
        finally { session.MarkSaved("liquify-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_with_pending_previews_and_show_original_keeps_document_and_history_unchanged()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); var session = Scene(); window.AddSession(session);
        var source = session.ActiveLayer!.Pixels!; var state = session.History.CurrentId; var before = source.Bytes;
        try
        {
            var task = Invoke(window); var dialog = window.OwnedWindows.OfType<LiquifyDialog>().Single(); await Preview(dialog);
            var original = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Show original"); original.IsChecked = true;
            Drag(dialog, new(160, 150), new(190, 150)); Assert.False(dialog.Field.HasDeformation); original.IsChecked = false;
            for (var i = 0; i < 5; i++) Drag(dialog, new(160 + i * 4, 150), new(180 + i * 4, 150));
            Assert.True(dialog.Field.HasDeformation); dialog.Close(false); await Finish(task);
            Assert.Equal(state, session.History.CurrentId); Assert.Same(source, session.ActiveLayer.Pixels); Assert.Equal(before, source.Bytes); Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Selection_protects_outside_and_a_result_is_rejected_after_external_document_change()
    {
        var window = new MainWindow { Width = 1280, Height = 860 }; window.Show(); var session = Scene(); window.AddSession(session); session.SelectRect(new(170, 100, 300, 230));
        try
        {
            var task = Invoke(window); var dialog = window.OwnedWindows.OfType<LiquifyDialog>().Single(); await Preview(dialog); FullBrush(dialog);
            Drag(dialog, new(160, 150), new(190, 150)); Assert.Equal(default, dialog.Field.DisplacementAt(160, 150)); Assert.True(dialog.Field.DisplacementAt(190, 150).X < -20);
            session.Fill(SKColors.Orange); var source = session.ActiveLayer!.Pixels; var state = session.History.CurrentId;
            Accept(dialog); await Finish(task); Assert.Equal(state, session.History.CurrentId); Assert.Same(source, session.ActiveLayer.Pixels);
        }
        finally { session.MarkSaved("liquify-ui.cmps", session.History.CurrentId); window.Close(); }
    }

    private static EditorSession Scene()
    {
        var document = new Document(400, 300) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var image = Pixels.NewWorkingColor(400, 300, document.Depth, document.Profile);
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.CornflowerBlue); using var paint = new SKPaint { Color = SKColors.Gold };
            for (var x = 0; x < 400; x += 20) canvas.DrawRect(x, 40, 8, 220, paint);
        }
        document.InsertAboveActive(Layer.Raster("Material", image)); return new(document);
    }
    private static void FullBrush(LiquifyDialog dialog)
    {
        foreach (var label in new[] { "Strength", "Hardness" })
        { var field = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == label); field.Focus(); dialog.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None); }
    }
    private static void Tool(LiquifyDialog dialog, LiquifyTool tool) => dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LiquifyTool").SelectedIndex = (int)tool;
    private static void Size(LiquifyDialog dialog, int size)
    {
        var field = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Size"); field.BeginEdit(); Dispatcher.UIThread.RunJobs();
        field.GetVisualDescendants().OfType<TextBox>().Single().Text = size.ToString(); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
    }
    private static Point At(LiquifyDialog dialog, SKPoint point) => dialog.View.TranslatePoint(dialog.View.ToView(point), dialog)!.Value;
    private static void Drag(LiquifyDialog dialog, SKPoint from, SKPoint to, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        dialog.MouseDown(At(dialog, from), MouseButton.Left, modifiers); dialog.MouseMove(At(dialog, to), RawInputModifiers.LeftMouseButton | modifiers);
        dialog.MouseUp(At(dialog, to), MouseButton.Left, modifiers); Dispatcher.UIThread.RunJobs();
    }
    private static Task Invoke(MainWindow window) => (Task)typeof(MainWindow).GetMethod("Liquify", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static void Accept(LiquifyDialog dialog) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Preview(LiquifyDialog dialog)
    {
        for (var i = 0; i < 300 && dialog.IsRendering; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.False(dialog.IsRendering); Assert.DoesNotContain("failed", dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LiquifyStatus").Text);
    }
    private static async Task Finish(Task task)
    {
        for (var i = 0; i < 300 && !task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(task.IsCompleted, "Liquify did not finish."); await task;
    }
}

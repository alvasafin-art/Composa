using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using Composa.App.Mcp;
using SkiaSharp;

namespace Composa.App.Tests;

public class MaskRefinementUiTests
{
    public static bool HasModel => TrimapMatting.IsAvailable;

    [AvaloniaFact(SkipUnless = nameof(HasModel), Skip = "Requires the pinned ViTMatte model")]
    public async Task Agent_refinement_refuses_a_stale_result_after_the_person_edits_the_document()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(1000, 750, SKColors.CornflowerBlue); window.AddSession(session); session.SelectRect(new(200, 150, 800, 600));
            var tools = new ComposaTools(window); var computation = tools.RefineSelection(method: "ai");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(15); session.Fill(SKColors.Orange); var state = session.History.CurrentId; var selection = session.Selection;
            for (var i = 0; i < 600 && !computation.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(computation.IsCompleted); await Assert.ThrowsAsync<ModelContextProtocol.McpException>(async () => await computation);
            Assert.Equal(state, session.History.CurrentId); Assert.Same(selection, session.Selection); Assert.Equal(SKColors.Orange, session.ActiveLayer!.Pixels!.GetPixel(400, 300));
        }
        finally { window.Close(); }
    }

    public static bool HasPhotoCorpus => HasModel && Environment.GetEnvironmentVariable("COMPOSA_MATTING_CORPUS_DIR") is { } path && File.Exists(Path.Combine(path, "input", "GT08.png"));

    [AvaloniaFact(SkipUnless = nameof(HasPhotoCorpus), Skip = "Requires COMPOSA_MATTING_CORPUS_DIR with the published Alpha Matting corpus")]
    public async Task Photographic_hair_preview_uses_the_real_RGB16_source_and_survives_accept_save_and_undo()
    {
        var root = Environment.GetEnvironmentVariable("COMPOSA_MATTING_CORPUS_DIR")!;
        using var decoded = SKBitmap.Decode(Path.Combine(root, "input", "GT08.png")); using var srgb = Pixels.DisplayCopy(decoded);
        var colors = Pixels.ConvertWorking(srgb, ColorDepth.Sixteen, Composa.ColorManagement.RgbProfile.DisplayP3);
        var document = new Document(colors.Width, colors.Height) { Depth = ColorDepth.Sixteen, Profile = Composa.ColorManagement.RgbProfile.DisplayP3 };
        var session = new EditorSession(document); session.AddImageLayer("Hair photograph", colors, fit: false);
        using var tri = SKBitmap.Decode(Path.Combine(root, "trimaps", "Trimap1", "GT08.png")); var selection = Pixels.NewMask(tri.Width, tri.Height);
        for (var y = 0; y < tri.Height; y++) for (var x = 0; x < tri.Width; x++) selection.SetPixel(x, y, new SKColor(0, 0, 0, tri.GetPixel(x, y).Red));
        session.Select(selection, Composa.Selections.SelectionMode.Replace); var state = session.History.CurrentId; var before = colors.Bytes;
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); window.AddSession(session);
        try
        {
            var task = Invoke(window); var dialog = window.OwnedWindows.OfType<SelectMaskDialog>().Single();
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Contains("Trimap")).SelectedItem = "White";
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "100%").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RefineStatus");
            for (var i = 0; i < 600 && status.Text != "Full-resolution preview"; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal("Full-resolution preview", status.Text);
            Assert.True(Screenshots.Save(dialog, "select-mask-real-hair-rgb16"));
            Accept(dialog); await Finish(task); Assert.NotSame(selection, session.Selection); Assert.Equal(before, colors.Bytes);
            Assert.Equal(SKColorType.RgbaF32, colors.ColorType); session.Undo(); Assert.Equal(state, session.History.CurrentId); Assert.Same(selection, session.Selection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Accepting_a_refined_selection_keeps_its_pixels_alive_through_disposal_save_and_undo()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(160, 100, SKColors.Blue, ColorDepth.Sixteen); window.AddSession(session); session.SelectRect(new(30, 20, 120, 80));
            var original = session.Selection; var pixels = session.ActiveLayer!.Pixels; var task = Invoke(window);
            var dialog = window.OwnedWindows.OfType<SelectMaskDialog>().Single();
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineMethod").SelectedItem = "Keep edge";
            Accept(dialog); await Finish(task);
            Assert.NotSame(original, session.Selection); Assert.Equal(255, session.Selection!.GetPixel(60, 40).Alpha); Assert.Same(pixels, session.ActiveLayer.Pixels);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cmps");
            try { Composa.IO.ProjectFile.Save(session.Document, path); Assert.True(new FileInfo(path).Length > 0); }
            finally { File.Delete(path); }
            session.Undo(); Assert.Same(original, session.Selection); session.Redo(); Assert.Equal(255, session.Selection!.GetPixel(60, 40).Alpha);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Local_brush_marks_can_be_undone_and_new_layer_output_has_an_editable_mask_and_one_undo_step()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(180, 120, SKColors.CornflowerBlue, ColorDepth.Sixteen); window.AddSession(session); session.SelectRect(new(30, 20, 150, 100));
            var image = session.ActiveLayer!.Pixels; var sourceId = session.ActiveLayer.Id; var state = session.History.CurrentId; var task = Invoke(window);
            var dialog = window.OwnedWindows.OfType<SelectMaskDialog>().Single(); Dispatcher.UIThread.RunJobs();
            var brush = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.Cast<object>().Contains("Remove background")); brush.SelectedItem = "Remove background";
            var preview = dialog.View;
            Point At(float x, float y) => preview.TranslatePoint(preview.ToView(new(x, y)), dialog)!.Value;
            dialog.MouseDown(At(60, 60), MouseButton.Left); dialog.MouseMove(At(90, 60), RawInputModifiers.LeftMouseButton); dialog.MouseUp(At(90, 60), MouseButton.Left);
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineMethod").SelectedItem, new[] { "Alpha matting", "AI matting" });
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Undo stroke").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.MouseDown(At(100, 60), MouseButton.Left); dialog.MouseUp(At(100, 60), MouseButton.Left);
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineOutput").SelectedItem = "New layer with mask";
            await Task.Delay(300); Dispatcher.UIThread.RunJobs(); Assert.True(Screenshots.Save(dialog, "select-mask-local-brush"));
            Accept(dialog); await Finish(task);
            var refined = session.ActiveLayer!; Assert.NotEqual(sourceId, refined.Id); Assert.NotNull(refined.Mask); Assert.Equal(SKColorType.RgbaF32, refined.Pixels!.ColorType);
            Assert.Equal(255, refined.Mask!.GetPixel(60, 60).Alpha); Assert.Equal(0, refined.Mask.GetPixel(100, 60).Alpha);
            Assert.Equal(0, refined.Mask.GetPixel(0, 0).Alpha); Assert.False(session.Document.Find(sourceId)!.Visible); Assert.Same(image, session.Document.Find(sourceId)!.Pixels);
            Assert.Equal(255, refined.Pixels.GetPixel(100, 60).Alpha); // Mask, not destructive pixel clearing.
            session.Undo(); Assert.Equal(state, session.History.CurrentId); Assert.Single(session.Document.Layers); Assert.True(session.ActiveLayer!.Visible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Preview_settings_and_source_changes_are_cancellable_and_do_not_commit_after_the_dialog_closes()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(320, 240, SKColors.Blue); window.AddSession(session); session.SelectRect(new(50, 40, 250, 200)); var state = session.History.CurrentId;
            var task = Invoke(window); var dialog = window.OwnedWindows.OfType<SelectMaskDialog>().Single();
            var mode = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineMethod");
            mode.SelectedItem = "Alpha matting"; await Task.Delay(190); Dispatcher.UIThread.RunJobs(); mode.SelectedItem = "Keep edge";
            var all = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Sample all visible layers"); all.IsChecked = true; all.IsChecked = false;
            dialog.Close(false); await Finish(task); await Task.Delay(210); Dispatcher.UIThread.RunJobs();
            Assert.Equal(state, session.History.CurrentId); Assert.Equal(255, session.Selection!.GetPixel(100, 100).Alpha); Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    private static Task Invoke(MainWindow window) => (Task)typeof(MainWindow).GetMethod("RefineSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static void Accept(SelectMaskDialog dialog) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Finish(Task task)
    {
        for (var i = 0; i < 300 && !task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(task.IsCompleted, "The refinement dialog did not finish."); await task;
    }
}

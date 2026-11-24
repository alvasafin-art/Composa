using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.LogicalTree;
using Composa.App.Controls;
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

    [AvaloniaTheory]
    [InlineData("menu")]
    [InlineData("footer")]
    [InlineData("context")]
    [InlineData("context selected row")]
    [InlineData("mcp")]
    [InlineData("refine dialog")]
    [InlineData("refine mcp")]
    public async Task Mask_commands_consume_the_selection_and_undo_with_the_mask(string entry)
    {
        var previous = Locale.Language;
        Locale.Language = "en";
        var window = new MainWindow { Width = 1280, Height = 900 };
        var session = EditorSession.NewCanvas(100, 80, SKColors.CornflowerBlue, ColorDepth.Sixteen);
        try
        {
            window.Show();
            window.AddSession(session);
            var layer = session.ActiveLayer!;
            var isRefine = entry.StartsWith("refine");
            if (isRefine)
            {
                session.AddMask(layer, hideAll: true);
                session.EditingMask = false;
            }
            if (entry == "context selected row")
            {
                var other = session.AddBlankLayer();
                session.SelectLayer(layer.Id, extend: true);
                session.SelectLayer(other.Id, range: true);
                Assert.Equal(other.Id, session.ActiveLayer!.Id);
                Assert.Contains(layer.Id, session.Document.SelectedLayerIds);
            }
            var activeBefore = session.ActiveLayer!.Id;
            var oldMask = layer.Mask;
            var original = layer.Pixels!;
            var bytes = original.Bytes;
            var selection = SelectionMask.FromRect(100, 80, new(20, 18, 70, 60));
            selection.SetPixel(20, 18, new SKColor(0, 0, 0, 109));
            session.Select(selection, Composa.Selections.SelectionMode.Replace);
            var before = session.History.CurrentId;
            var count = session.History.Count;
            var selectionChanges = 0;
            session.SelectionChanged += () => selectionChanges++;
            Dispatcher.UIThread.RunJobs();

            switch (entry)
            {
                case "menu":
                    window.GetLogicalDescendants().OfType<MenuItem>().Single(i => (string?)i.Header == "Add Layer Mask")
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    break;
                case "footer":
                    window.GetVisualDescendants().OfType<Button>()
                        .Single(b => AutomationProperties.GetName(b) == "Add layer mask (Alt: hide all)")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                case "context":
                case "context selected row":
                    var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
                    var row = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag == layer);
                    var menu = row.ContextMenu!;
                    menu.Open(row);
                    menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Add Mask")
                        .Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Reveal All (White)")
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.Close();
                    break;
                case "mcp":
                    await new ComposaTools(window).LayerMask(layer.Name, "add");
                    break;
                case "refine dialog":
                    var task = Invoke(window);
                    var dialog = window.OwnedWindows.OfType<SelectMaskDialog>().Single();
                    dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineMethod").SelectedItem = "Keep edge";
                    dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineOutput").SelectedItem = "Layer mask";
                    Accept(dialog);
                    await Finish(task);
                    break;
                case "refine mcp":
                    await Finish(new ComposaTools(window).RefineSelection(method: "keep", output: "mask"));
                    break;
            }

            var mask = Assert.IsType<SKBitmap>(layer.Mask);
            Assert.Null(session.Selection);
            Assert.Equal(1, selectionChanges);
            Assert.Same(mask, session.EditingMaskBitmap);
            Assert.Equal(109, mask.GetPixel(20, 18).Alpha);
            Assert.Equal(255, mask.GetPixel(25, 25).Alpha);
            Assert.Equal(0, mask.GetPixel(5, 5).Alpha);
            Assert.Same(original, layer.Pixels);
            Assert.Equal(bytes, layer.Pixels!.Bytes);
            Assert.Equal(count + 1, session.History.Count);

            session.Undo();
            Assert.Equal(before, session.History.CurrentId);
            Assert.Same(selection, session.Selection);
            Assert.Equal(activeBefore, session.ActiveLayer!.Id);
            Assert.Same(oldMask, session.Document.Find(layer.Id)!.Mask);
            session.Redo();
            Assert.Null(session.Selection);
            Assert.Same(mask, session.ActiveLayer!.Mask);
            Assert.Same(mask, session.EditingMaskBitmap);
            Assert.Same(original, session.ActiveLayer.Pixels);
        }
        finally
        {
            Locale.Language = previous;
            session.MarkSaved("mask.cmps", session.History.CurrentId);
            window.Close();
        }
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
            var original = session.Selection;
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineOutput").SelectedItem = "Layer mask";
            var mode = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineMethod");
            mode.SelectedItem = "Alpha matting"; await Task.Delay(190); Dispatcher.UIThread.RunJobs(); mode.SelectedItem = "Keep edge";
            var all = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Sample all visible layers"); all.IsChecked = true; all.IsChecked = false;
            dialog.Close(false); await Finish(task); await Task.Delay(210); Dispatcher.UIThread.RunJobs();
            Assert.Equal(state, session.History.CurrentId);
            Assert.Same(original, session.Selection);
            Assert.Null(session.ActiveLayer!.Mask);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Foreground_selection_cleans_edge_colors_only_after_the_user_enables_decontamination()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        try
        {
            var session = EditorSession.NewCanvas(80, 40);
            var photo = Pixels.NewColor(80, 40);
            var mask = Pixels.NewMask(80, 40);
            for (var y = 0; y < 40; y++) for (var x = 0; x < 80; x++)
            {
                var coverage = x is >= 14 and < 44 ? 255 : x is >= 44 and < 48 ? 102 : 0;
                photo.SetPixel(x, y, coverage == 255 ? new(227, 214, 181) : coverage == 102 ? new(112, 197, 107) : new(36, 186, 59));
                mask.SetPixel(x, y, new(0, 0, 0, (byte)coverage));
            }
            var layer = session.AddImageLayer("Original", photo, new(40, 20), fit: false);
            var original = layer.Pixels!;
            var bytes = original.GetPixelSpan().ToArray();
            session.ApplyAiSelection(Composa.AI.AiTaskKind.ObjectSelection, mask);
            window.AddSession(session);

            var task = Invoke(window);
            var dialog = window.OwnedWindows.OfType<SelectMaskDialog>().Single();
            Dispatcher.UIThread.RunJobs();
            var output = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineOutput");
            var decontaminate = dialog.GetVisualDescendants().OfType<Composa.App.Controls.SliderField>()
                .Single(c => c.Label == "Decontaminate");
            Assert.Equal(0, decontaminate.Value);
            Assert.True(output.IsEnabled);
            Assert.Equal("Selection", output.SelectedItem);
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RefineMethod").SelectedItem = "Keep edge";
            decontaminate.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End });
            Assert.Equal(100, decontaminate.Value);
            Assert.Equal("New layer with mask", output.SelectedItem);
            Accept(dialog);
            await Finish(task);

            var cutout = session.ActiveLayer!;
            Assert.NotEqual(layer.Id, cutout.Id);
            Assert.Equal(102, cutout.Mask!.GetPixel(45, 20).Alpha);
            Assert.True(cutout.Pixels!.GetPixel(45, 20).Red > original.GetPixel(45, 20).Red + 50);
            Assert.Equal(bytes, original.GetPixelSpan().ToArray());
            Assert.Same(original, session.Document.Find(layer.Id)!.Pixels);
            Assert.False(session.Document.Find(layer.Id)!.Visible);
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

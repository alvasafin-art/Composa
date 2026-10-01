using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Automation;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiToolWorkflowTests
{
    [Fact]
    public void Reusable_script_recolors_every_other_square_in_panel_order_without_rasterizing()
    {
        var s = EditorSession.NewCanvas(600, 400);
        for (var i = 0; i < 5; i++) s.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), SKRect.Create(i * 60, 0, 40, 40));
        var rectangle = s.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Green, 0), SKRect.Create(0, 100, 80, 40))!;
        var history = s.History.Count;
        new JavaScriptRuntime().Execute(s, """
            const squares = app.activeDocument.layers.filter(l => l.kind === 'shape' && l.shape.kind === 'Rectangle' && Math.abs(l.transform.width-l.transform.height)<0.01).reverse();
            squares.forEach((l,i) => { if(i%2===0) l.setShapeColor('#FFFF00'); });
            """);
        var squares = s.Document.Layers.Where(l => l.Shape != null && l.Transform.Width == l.Transform.Height).Reverse().ToArray();
        Assert.Equal(5, squares.Length);
        for (var i = 0; i < 5; i++) Assert.Equal((uint)(i % 2 == 0 ? SKColors.Yellow : SKColors.Blue), squares[i].Shape!.Fill);
        Assert.Equal((uint)SKColors.Green, rectangle.Shape!.Fill);
        Assert.Equal(history + 1, s.History.Count); s.Undo();
        Assert.All(s.Document.Layers.Where(l => l.Shape != null && l.Transform.Width == l.Transform.Height), l => Assert.Equal((uint)SKColors.Blue, l.Shape!.Fill));
    }

    [Fact]
    public void Object_selection_upload_is_cropped_to_roi_and_returned_mask_is_placed_in_canvas_space()
    {
        var s = EditorSession.NewCanvas(300, 200, SKColors.White); s.SelectRect(new SKRect(80, 50, 140, 100));
        using var inputs = AiTaskInputPreparer.Prepare(s, new() { Task = AiTaskKind.ObjectSelection });
        Assert.Equal((60, 50), (inputs.SourceImage.Width, inputs.SourceImage.Height));
        Assert.Equal((300, 200), (inputs.ContextImage.Width, inputs.ContextImage.Height));
        var generated = Pixels.NewColor(120, 100); generated.Erase(SKColors.White);
        AiTaskService.Insert(new EditorCommandService(s), AiTaskKind.ObjectSelection, AiOutputMode.Selection, [generated], inputs.TargetBounds, inputs);
        Assert.Equal((byte)255, s.Selection!.GetPixel(90, 60).Alpha);
        Assert.Equal((byte)0, s.Selection.GetPixel(20, 20).Alpha);
        Assert.Equal((byte)0, s.Selection.GetPixel(141, 70).Alpha);
        s.Deselect(); using var full = AiTaskInputPreparer.Prepare(s, new() { Task = AiTaskKind.ObjectSelection });
        Assert.Null(full.SegmentationSourceBounds); Assert.Equal((300, 200), (full.SourceImage.Width, full.SourceImage.Height));
    }

    [AvaloniaFact]
    public void Crop_starts_with_largest_aspect_frame_and_new_tools_have_distinct_icons()
    {
        var w = new MainWindow { Width = 1280, Height = 900 }; w.Settings.CheckForUpdates = false; w.Show();
        var s = EditorSession.NewCanvas(600, 400); w.AddSession(s); s.CropRatio = "1:1"; w.SelectTool(Tool.Crop);
        Assert.Equal(new SKRect(100, 0, 500, 400), w.Canvas.CropRect);
        s.CropRatio = "16:9"; w.Canvas.ChangeCropRatio(); Assert.Equal(new SKRect(0, 31.25f, 600, 368.75f), w.Canvas.CropRect);
        w.SelectTool(Tool.Bucket); Assert.Same(w.RailButton(Tool.Gradient), w.RailButton(Tool.Bucket));
        Assert.Equal(Icons.Bucket, w.RailButton(Tool.Bucket).Current!.Icon);
        Assert.False(w.RailButton(Tool.RemoveObject).IsEnabled);
        w.AiTasks.SetConnectedForTests(); Dispatcher.UIThread.RunJobs(); Assert.True(w.RailButton(Tool.RemoveObject).IsEnabled);
        w.SelectTool(Tool.ObjectSelectionAi); Assert.Equal(Icons.ObjectSelectAi, w.RailButton(Tool.Wand).Current!.Icon);
        Assert.NotEqual(Icons.ObjectSelect, Icons.ObjectSelectAi);
        w.SelectTool(Tool.RemoveObject); s.SelectRect(new SKRect(20, 20, 100, 100));
        Assert.False(w.AiFloatingVisible); Screenshots.Save(w, "ai-remove-tool-without-floating-panel"); w.Close();
    }

    [AvaloniaFact]
    public void Ai_rectangle_and_removal_brush_submit_only_after_pointer_release()
    {
        var canvas = new CanvasView { Session = EditorSession.NewCanvas(600, 400), AiToolsAvailable = () => true };
        var w = new Window { Width = 900, Height = 650, Content = canvas }; w.Show(); canvas.Fit(); Dispatcher.UIThread.RunJobs();
        var completed = new List<AiTaskKind>(); canvas.AiSelectionCompleted += completed.Add;
        Point At(float x, float y) => canvas.TranslatePoint(canvas.ToScreen(new SKPoint(x, y)), w)!.Value;
        foreach (var tool in new[] { Tool.ObjectSelectionAi, Tool.RemoveObject })
        {
            canvas.Session.Tool = tool; canvas.ToolChanged(); var count = completed.Count;
            w.MouseDown(At(100, 100), MouseButton.Left); w.MouseMove(At(160, 140), RawInputModifiers.LeftMouseButton);
            Assert.Equal(count, completed.Count);
            w.MouseUp(At(160, 140), MouseButton.Left); Assert.Equal(count + 1, completed.Count);
            Assert.NotNull(canvas.Session.Selection); Assert.False(canvas.Session.IsInteracting);
        }
        Assert.Equal(new[] { AiTaskKind.ObjectSelection, AiTaskKind.RemoveObject }, completed); w.Close();
    }

    [AvaloniaFact]
    public async Task Expand_hides_prompt_and_selection_mode_and_defaults_to_empty_only()
    {
        var w = new MainWindow { Width = 1280, Height = 900 }; w.Settings.CheckForUpdates = false; w.Show();
        var settings = new Settings { AiExpansionMode = AiExpansionMode.WholeImage };
        foreach (var selected in new[] { false, true })
        {
            var pending = AiDialogs.Prompt(w, AiTaskKind.GenerativeExpand, settings, 800, 600, "old fill prompt", w.AiTasks, hasSelection: selected);
            Dispatcher.UIThread.RunJobs(); var dialog = Assert.Single(w.OwnedWindows);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.IsEffectivelyVisible);
            var labels = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Equal(!selected, labels.Contains("Expand mode"));
            Screenshots.Save(dialog, selected ? "expand-selection-no-mode" : "expand-empty-only-fixed-prompt");
            dialog.Close(); Assert.Null(await pending);
        }
        w.Close();
    }

    [Fact]
    public void Agent_context_pages_valid_json_without_hiding_current_shape_colors()
    {
        var s = EditorSession.NewCanvas(600, 400);
        for (var i = 0; i < 30; i++) s.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), SKRect.Create(i, i, 40, 40));
        var ids = new HashSet<string>(); var offset = 0;
        while (true)
        {
            var json = JavaScriptRuntime.DescribeCompact(s, offset); Assert.InRange(json.Length, 1, 4200);
            using var doc = System.Text.Json.JsonDocument.Parse(json); var root = doc.RootElement;
            foreach (var layer in root.GetProperty("layers").EnumerateArray()) Assert.True(ids.Add(layer.GetProperty("id").GetString()!));
            if (!root.GetProperty("truncated").GetBoolean()) break;
            var next = root.GetProperty("nextOffset").GetInt32(); Assert.True(next > offset); offset = next;
        }
        Assert.Equal(31, ids.Count);
    }
}

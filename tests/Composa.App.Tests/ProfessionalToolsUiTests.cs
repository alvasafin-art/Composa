using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class ProfessionalToolsUiTests
{
    private static Task Invoke(MainWindow window, string name) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static Point At(MainWindow window, CanvasView canvas, float x, float y) => canvas.TranslatePoint(canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
    private static void Click(MainWindow window, CanvasView canvas, float x, float y)
    {
        var p = At(window, canvas, x, y); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    [AvaloniaFact]
    public void Brush_flow_spacing_pressure_and_presets_use_existing_inline_controls()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var session = EditorSession.NewCanvas(300, 200, SKColors.White); window.AddSession(session);
        window.SelectTool(Tool.Brush); Dispatcher.UIThread.RunJobs();
        var fields = window.GetVisualDescendants().OfType<SliderField>().ToArray();
        var flow = fields.Single(f => f.Label == "Flow"); var spacing = fields.Single(f => f.Label == "Spacing"); var size = fields.Single(f => f.Label == "Size");
        Assert.Equal(size.TranslatePoint(default, window)!.Value.Y, flow.TranslatePoint(default, window)!.Value.Y);
        Assert.Equal(size.TranslatePoint(default, window)!.Value.Y, spacing.TranslatePoint(default, window)!.Value.Y);
        flow.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None); Assert.Equal(.99, session.Brush.Flow, 2);
        Screenshots.Save(window, "professional-brush-options"); window.Close();
    }
    [AvaloniaFact]
    public void Pen_mouse_clicks_and_drag_handles_create_a_closed_editable_curve()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var session = EditorSession.NewCanvas(300, 200, SKColors.White); window.AddSession(session);
        window.SelectTool(Tool.Pen); Dispatcher.UIThread.RunJobs(); var canvas = window.GetVisualDescendants().OfType<CanvasView>().Single();
        Click(window, canvas, 40, 40);
        window.MouseDown(At(window, canvas, 240, 40), MouseButton.Left); window.MouseMove(At(window, canvas, 270, 70)); window.MouseUp(At(window, canvas, 270, 70), MouseButton.Left);
        Click(window, canvas, 200, 160); Click(window, canvas, 40, 40);
        Assert.NotNull(session.ActiveLayer!.Shape?.Path); Assert.True(session.ActiveLayer.Shape!.Path!.Closed);
        Assert.Equal(3, session.ActiveLayer.Shape.Path.Nodes.Length); Assert.NotEqual(session.ActiveLayer.Shape.Path.Nodes[1].X, session.ActiveLayer.Shape.Path.Nodes[1].OutX);
        Assert.False(session.IsInteracting); Screenshots.Save(window, "professional-pen-curve");
        var shape = session.ActiveLayer.Shape; var node = shape.Path.Nodes[0];
        var point = session.ActiveLayer.Matrix.MapPoint((float)node.X * session.ActiveLayer.Pixels!.Width, (float)node.Y * session.ActiveLayer.Pixels.Height);
        Click(window, canvas, point.X, point.Y); window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Equal(2, session.ActiveLayer.Shape!.Path!.Nodes.Length); session.Undo(); Assert.Equal(3, session.ActiveLayer!.Shape!.Path!.Nodes.Length);
        session.Undo(); Assert.Null(session.ActiveLayer!.Shape); window.Close();
    }
    [AvaloniaFact]
    public async Task Content_aware_workspace_and_mask_refinement_cancel_without_changing_pixels()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var session = EditorSession.NewCanvas(160, 100, SKColors.White); window.AddSession(session);
        session.SelectRect(new SKRect(30, 25, 70, 65)); var original = session.ActiveLayer!.Pixels; var state = session.History.CurrentId;
        var fill = Invoke(window, "ContentAwareFillWorkspace"); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Assert.Equal("Content-Aware Fill", dialog.Title); Assert.True(session.IsPreviewing);
        Assert.True(dialog.CanResize); Assert.True(dialog.Width >= 900); Assert.True(dialog.Height >= 560);
        Screenshots.Save(dialog, "professional-content-aware-workspace"); dialog.Close(false); await fill;
        Assert.False(session.IsInteracting); Assert.Same(original, session.ActiveLayer!.Pixels); Assert.Equal(state, session.History.CurrentId);
        var refine = Invoke(window, "RefineSelection"); Dispatcher.UIThread.RunJobs();
        dialog = window.OwnedWindows.OfType<DialogWindow>().Single(); Assert.Equal("Select and Mask", dialog.Title);
        Screenshots.Save(dialog, "professional-select-mask"); dialog.Close(false); await refine;
        Assert.Equal(state, session.History.CurrentId); Assert.Same(original, session.ActiveLayer.Pixels); window.Close();
    }
    [AvaloniaFact]
    public void Object_selection_lists_both_real_local_models_and_keeps_the_current_choice()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); window.AddSession(EditorSession.NewCanvas(160, 100, SKColors.White));
        window.Session!.WandMode = WandMode.Object; window.SelectTool(Tool.Wand); Dispatcher.UIThread.RunJobs();
        var choice = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ObjectSelectionModel");
        Assert.Contains(choice.Items.Cast<string>(), s => s == "EfficientSAM S · Quality · Local");
        Assert.Contains(choice.Items.Cast<string>(), s => s == "MobileSAM · Fast · Local"); Assert.Contains(choice.Items.Cast<string>(), s => s == "EfficientSAM Ti · Fast · Local");
        choice.SelectedIndex = choice.Items.Cast<string>().ToList().IndexOf("EfficientSAM Ti · Fast · Local");
        Assert.Equal(ObjectSelectionSource.EfficientSamTi, window.Settings.ObjectSelectionModel);
        Screenshots.Save(window, "professional-object-selection-models"); window.Close();
    }
    [AvaloniaFact]
    public async Task Content_aware_preview_accepts_a_repair_layer_and_undo_restores_the_original_stack()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var session = EditorSession.NewCanvas(80, 60, SKColors.White); window.AddSession(session);
        session.SelectRect(new SKRect(25, 15, 45, 35)); session.Fill(SKColors.Black); var original = session.ActiveLayer!; var pixels = original.Pixels;
        var fill = Invoke(window, "ContentAwareFillWorkspace"); Dispatcher.UIThread.RunJobs(); var dialog = window.OwnedWindows.OfType<DialogWindow>().Single();
        var preview = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Preview");
        var accept = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "OK");
        var center = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(center, MouseButton.Left); dialog.MouseUp(center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 500 && !accept.IsEnabled; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(accept.IsEnabled); Screenshots.Save(dialog, "professional-content-aware-result");
        dialog.Close(true); for (var i = 0; i < 500 && !fill.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); } await fill;
        Assert.Equal(2, session.Document.Layers.Count); Assert.Same(pixels, session.Document.Find(original.Id)!.Pixels);
        using var rendered = session.Flatten(); Assert.Equal(SKColors.White, rendered.GetPixel(35, 25));
        Assert.Equal("New Retouch Layer", session.History.UndoName); session.Undo(); Assert.Single(session.Document.Layers); Assert.Same(pixels, session.ActiveLayer!.Pixels); window.Close();
    }
    [AvaloniaFact]
    public async Task Smart_filter_dialog_previews_and_cancel_restores_the_unfiltered_source()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var session = EditorSession.NewCanvas(80, 60, SKColors.Red); window.AddSession(session);
        var source = session.ActiveLayer!.Pixels;
        var method = typeof(MainWindow).GetMethod("SmartFilter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var edit = (Task)method.Invoke(window, new object?[] { Composa.Filters.FilterKind.GaussianBlur, null, null })!; Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.OfType<DialogWindow>().Single();
        var radius = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Radius"); radius.Focus(); dialog.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
        Assert.NotNull(session.ActiveLayer.FilterSource); Assert.Single(session.ActiveLayer.SmartFilters); Assert.NotSame(source, session.ActiveLayer.Pixels);
        Screenshots.Save(dialog, "professional-smart-filter-preview"); dialog.Close(false); await edit;
        Assert.Same(source, session.ActiveLayer!.Pixels); Assert.Null(session.ActiveLayer.FilterSource); Assert.False(session.IsInteracting); window.Close();
    }
    [AvaloniaFact]
    public void Healing_tool_opens_its_bar_and_requires_a_donor_before_painting()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var session = EditorSession.NewCanvas(80, 60, SKColors.White); window.AddSession(session);
        window.SelectTool(Tool.HealingBrush); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Healing Brush");
        Assert.False(session.BeginStroke(new SKPoint(35, 25), out var problem)); Assert.Contains("Alt", problem!);
        Screenshots.Save(window, "professional-healing-brush"); window.Close();
    }
    [AvaloniaFact]
    public void Ctrl_pen_starts_a_new_path_instead_of_moving_the_active_layer_and_draft_stays_in_its_document()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show(); var first = EditorSession.NewCanvas(100, 80, SKColors.White); window.AddSession(first);
        window.SelectTool(Tool.Pen); Dispatcher.UIThread.RunJobs(); var canvas = window.GetVisualDescendants().OfType<CanvasView>().Single();
        var transform = first.ActiveLayer!.Transform; var p = At(window, canvas, 20, 20);
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.Control); window.MouseUp(p, MouseButton.Left, RawInputModifiers.Control); Click(window, canvas, 70, 50);
        Assert.Equal(transform, first.ActiveLayer.Transform); Assert.Single(first.Document.Layers);
        var second = EditorSession.NewCanvas(120, 90, SKColors.Black); window.AddSession(second); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, first.Document.Layers.Count); Assert.NotNull(first.ActiveLayer!.Shape?.Path); Assert.Single(second.Document.Layers); window.Close();
    }
}

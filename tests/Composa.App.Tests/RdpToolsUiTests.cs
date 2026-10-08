using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class RdpToolsUiTests
{
    [AvaloniaFact]
    public async Task Generate_image_remembers_the_prompt_even_when_cancelled_and_after_settings_reload()
    {
        var settings = new Settings { GenerateImagePrompt = "previous landscape" }; var owner = new Window(); owner.Show();
        var pending = AiDialogs.Prompt(owner, AiTaskKind.GenerateImage, settings, 640, 480);
        var dialog = owner.OwnedWindows.Single(); Dispatcher.UIThread.RunJobs();
        var prompt = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn);
        Assert.Equal("previous landscape", prompt.Text); prompt.Text = "новый пейзаж"; dialog.Close(false); Assert.Null(await pending);
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!; Assert.Equal("новый пейзаж", loaded.GenerateImagePrompt); owner.Close();
    }

    [AvaloniaFact]
    public void Curves_keep_point_selection_and_support_numeric_input_arrows_and_delete()
    {
        var editor = new CurveEditor { Width = 300, Height = 300 }; var window = new Window { Width = 340, Height = 340, Content = editor }; window.Show(); Dispatcher.UIThread.RunJobs();
        Point At(double x, double y) => editor.TranslatePoint(new Point(x, y), window)!.Value;
        window.MouseDown(At(150, 150), MouseButton.Left); window.MouseUp(At(150, 150), MouseButton.Left);
        Assert.NotNull(editor.SelectedPoint); editor.MoveSelected(120, 170); Assert.Equal(new Composa.Filters.CurvePoint(120, 170), editor.SelectedPoint);
        window.KeyPress(Key.Up, RawInputModifiers.Shift, PhysicalKey.ArrowUp, null); Assert.Equal(180, editor.SelectedPoint!.Value.Y);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null); Assert.Equal(121, editor.SelectedPoint!.Value.X);
        Screenshots.Save(window, "rdp-curves-selected-point"); window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Null(editor.SelectedPoint); Assert.Equal(2, editor.Curves.Channels[0].Length); window.Close();
    }

    [AvaloniaFact]
    public void Alt_click_converts_pen_points_both_ways_and_is_undoable()
    {
        var window = new MainWindow { Width = 1280, Height = 850 }; window.Show(); var s = EditorSession.NewCanvas(300, 200, SKColors.White); window.AddSession(s); window.SelectTool(Tool.Pen); Dispatcher.UIThread.RunJobs();
        Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
        void Click(float x, float y, RawInputModifiers modifiers = RawInputModifiers.None) { window.MouseDown(At(x, y), MouseButton.Left, modifiers); window.MouseUp(At(x, y), MouseButton.Left, modifiers); }
        Click(35, 30); Click(230, 30); Click(200, 155); Click(200, 155);
        var original = s.ActiveLayer!.Shape!.Path!.Nodes[0]; var state = s.History.CurrentId;
        Click(35, 30, RawInputModifiers.Alt); var smooth = s.ActiveLayer!.Shape!.Path!.Nodes[0];
        Assert.NotEqual(smooth.X, smooth.OutX); Assert.NotEqual(smooth.Y, smooth.InY); Assert.False(s.IsInteracting);
        Click(35, 30, RawInputModifiers.Alt); var corner = s.ActiveLayer!.Shape!.Path!.Nodes[0]; Assert.Equal(corner.X, corner.OutX); Assert.Equal(corner.Y, corner.InY);
        s.Undo(); Assert.NotEqual(s.ActiveLayer!.Shape!.Path!.Nodes[0].X, s.ActiveLayer.Shape.Path.Nodes[0].OutX);
        s.Undo(); Assert.Equal(state, s.History.CurrentId); Assert.Equal(original, s.ActiveLayer!.Shape!.Path!.Nodes[0]); window.Close();
    }

    [AvaloniaFact]
    public void Transforming_a_selected_group_does_not_autoselect_its_child()
    {
        var window = new MainWindow { Width = 1280, Height = 850 }; window.Show(); var s = EditorSession.NewCanvas(300, 200, SKColors.White); window.AddSession(s);
        var a = s.AddShape(new(ShapeKind.Rectangle, (uint)SKColors.Red, 0), new SKRect(40, 40, 80, 80));
        var b = s.AddShape(new(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new SKRect(120, 40, 160, 80)); s.SelectLayer(a.Id); s.SelectLayer(b.Id, extend: true); s.GroupSelectedLayers();
        var group = s.ActiveLayer!; var first = a.Matrix; var second = b.Matrix; var state = s.History.CurrentId;
        s.View = s.View with { Snap = false };
        window.Canvas.Focus(); window.KeyPress(Key.T, RawInputModifiers.Control, PhysicalKey.T, null); Dispatcher.UIThread.RunJobs();
        Assert.True(window.Canvas.ShowTransformControls); Assert.Equal(Tool.Move, s.Tool);
        Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
        window.MouseDown(At(60, 60), MouseButton.Left); window.MouseMove(At(80, 75), RawInputModifiers.LeftMouseButton); window.MouseUp(At(80, 75), MouseButton.Left);
        Assert.Equal(group.Id, s.ActiveLayer!.Id); Assert.Equal(first.TransX + 20, a.Matrix.TransX, 3); Assert.Equal(second.TransY + 15, b.Matrix.TransY, 3);
        s.Undo(); Assert.Equal(state, s.History.CurrentId);
        var oldWidths = s.ActiveLayer!.Children.Select(child => child.ControlBounds.Width).ToArray();
        window.MouseDown(At(160, 80), MouseButton.Left); window.MouseMove(At(200, 100), RawInputModifiers.LeftMouseButton); window.MouseUp(At(200, 100), MouseButton.Left);
        Assert.Equal(group.Id, s.ActiveLayer!.Id); Assert.All(s.ActiveLayer.Children.Select((child, index) => child.ControlBounds.Width > oldWidths[index]), Assert.True);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Upscale_lists_actual_models_and_saves_the_selected_seedvr_backend_and_slot()
    {
        var settings = new Settings(); var service = new AiTaskService(() => settings.ComfyServerUrl, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var native = service.Engines.Find("seedvr2-native")!;
        service.SetConnectedForTests(new() { NodeTypes = new(native.RequiredNodeTypes), ModelChoices = new() {
            ["UNETLoader.unet_name"] = ["flux-2-klein-9b.safetensors", "shared/seedvr2_3b_fp16.safetensors"],
            ["UpscaleModelLoader.model_name"] = ["4x-UltraSharp.pth"] } });
        var choices = UpscaleModels.Choices(service); Assert.Single(choices); Assert.Contains("seedvr2", choices[0].Model); Assert.DoesNotContain(choices, choice => choice.Model.Contains("flux"));
        var owner = new Window(); owner.Show(); var pending = AiDialogs.Upscale(owner, settings, 100, 100, false, service.SelectedEngine, service);
        var dialog = owner.OwnedWindows.Single(); Dispatcher.UIThread.RunJobs(); Screenshots.Save(dialog, "rdp-upscale-models");
        dialog.Close(true); Assert.True(await pending); Assert.Equal(native.Id, settings.AiTaskEngineIds[nameof(AiTaskKind.Upscale)]);
        Assert.Equal(choices[0].Model, settings.ComfyModelsFor(settings.ComfyServerUrl)[choices[0].Slot.Key]); owner.Close();
    }
}

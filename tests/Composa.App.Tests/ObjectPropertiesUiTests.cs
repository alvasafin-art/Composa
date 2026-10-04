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

public class ObjectPropertiesUiTests
{
    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Selected_shape_properties_are_inline_and_consecutive_edits_undo_together()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(session);
        var layer = session.AddShape(new ShapeStyle(ShapeKind.RoundedRectangle, (uint)new SKColor(0xFF, 0x8A, 0x3D), 20), new SKRect(150, 100, 450, 300))!;
        window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
        SliderField Field(string label) => window.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == label);
        CheckBox Check(string label) => window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == label);
        Assert.Empty(window.OwnedWindows);
        Assert.Equal(20, Field("Corner radius").Value);
        Assert.Equal(2, Field("Stroke width").Value);
        Assert.True(Field("Corner radius").TranslatePoint(new Point(Field("Corner radius").Bounds.Width, 0), window)!.Value.X < 1190);
        Field("Corner radius").Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(22, session.ActiveLayer!.Shape!.CornerRadius);
        Click(window, Check("Stroke")); Assert.NotNull(session.ActiveLayer.Shape.Stroke);
        Screenshots.Save(window, "shape-properties-inline-move");
        Assert.Equal("Shape Properties", session.History.UndoName);
        Assert.False(session.IsInteracting);
        session.Undo();
        Assert.Equal("Rectangle", session.History.UndoName);
        Assert.Equal(20, session.ActiveLayer!.Shape!.CornerRadius); Assert.Null(session.ActiveLayer.Shape.Stroke);
        Assert.Equal(20, Field("Corner radius").Value); Assert.False(Check("Stroke").IsChecked);
        session.Redo(); Assert.Equal(22, session.ActiveLayer!.Shape!.CornerRadius);
        window.SelectTool(Tool.Shape); Dispatcher.UIThread.RunJobs();
        Assert.Equal(22, Field("Corner radius").Value);
        Click(window, Check("Fill")); Assert.False(session.ActiveLayer.Shape.FillEnabled);
        Screenshots.Save(window, "shape-properties-inline-shape");
        session.SelectLayer(session.Document.Layers[0].Id); Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "Stroke");
        window.Close();
    }

    [AvaloniaFact]
    public void Shape_color_picker_previews_and_cancel_restores_the_original_pixels()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(session);
        var layer = session.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Red, 0), new SKRect(150, 100, 450, 300))!;
        window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
        var pixels = layer.Pixels;
        var swatch = window.GetVisualDescendants().OfType<Border>().Single(b => ToolTip.GetTip(b) as string == "Fill color");
        Click(window, swatch); var dialog = window.OwnedWindows.Last();
        Assert.Equal("Fill Color", dialog.Title);
        var picker = dialog.GetVisualDescendants().OfType<ColorView>().Single();
        picker.Color = Avalonia.Media.Colors.Blue; Dispatcher.UIThread.RunJobs();
        Assert.Equal((uint)SKColors.Blue, session.ActiveLayer!.Shape!.Fill);
        dialog.Close(false); Dispatcher.UIThread.RunJobs();
        Assert.Equal((uint)SKColors.Red, session.ActiveLayer!.Shape!.Fill);
        Assert.Same(pixels, session.ActiveLayer.Pixels); Assert.Equal("Rectangle", session.History.UndoName);
        Assert.False(session.IsInteracting);
        window.Close();
    }

    [AvaloniaFact]
    public void Gradient_effect_uses_existing_fields_and_preview_cancels_with_the_dialog()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(session);
        var layer = session.AddShape(new ShapeStyle(ShapeKind.RoundedRectangle, (uint)SKColors.Red, 20), new SKRect(150, 100, 450, 300))!;
        session.AddEffect(layer, LayerEffectKind.GradientOverlay, commit: false);
        _ = EffectsDialog.Edit(window, session, layer, LayerEffectKind.GradientOverlay); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Last(); Assert.Equal("Gradient Overlay", dialog.Title);
        Assert.Equal(3, dialog.GetVisualDescendants().OfType<SliderField>().Count());
        Assert.Single(dialog.GetVisualDescendants().OfType<AngleDial>());
        var styles = dialog.GetVisualDescendants().OfType<ComboBox>().Single(); styles.SelectedIndex = 1;
        Assert.True(layer.Effects!.GradientOverlay!.Radial);
        Click(dialog, dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Reverse"));
        Assert.True(layer.Effects.GradientOverlay.Reverse);
        Screenshots.Save(dialog, "gradient-overlay-dialog");
        dialog.Close(false); Dispatcher.UIThread.RunJobs(); session.Cancel();
        Assert.Null(session.ActiveLayer!.Effects); Assert.False(session.IsInteracting);
        session.AddEffect(session.ActiveLayer, LayerEffectKind.GradientOverlay);
        Assert.Contains(session.ActiveLayer.Effects!.Kinds, k => k == LayerEffectKind.GradientOverlay);
        Screenshots.Save(window, "gradient-overlay-layer-row");
        window.Close();
    }
}

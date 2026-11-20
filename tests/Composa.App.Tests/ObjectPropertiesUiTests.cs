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
        Button Eye(string label) => window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == label+"Visibility");
        Assert.Empty(window.OwnedWindows);
        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TransformProperties"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(20, Field("Corner radius").Value);
        Assert.Equal(2, Field("Stroke width").Value);
        Assert.True(Field("Corner radius").TranslatePoint(new Point(Field("Corner radius").Bounds.Width, 0), window)!.Value.X < 1190);
        Field("Corner radius").Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(22, session.ActiveLayer!.Shape!.CornerRadius);
        Click(window, Eye("Stroke")); Assert.NotNull(session.ActiveLayer.Shape.Stroke);
        Screenshots.Save(window, "shape-properties-inline-move");
        Assert.Equal("Shape Properties", session.History.UndoName);
        Assert.False(session.IsInteracting);
        session.Undo();
        Assert.Equal("Rectangle", session.History.UndoName);
        Assert.Equal(20, session.ActiveLayer!.Shape!.CornerRadius); Assert.Null(session.ActiveLayer.Shape.Stroke);
        Assert.Equal(20, Field("Corner radius").Value); Assert.Null(session.ActiveLayer.Shape.Stroke);
        session.Redo(); Assert.Equal(22, session.ActiveLayer!.Shape!.CornerRadius);
        window.SelectTool(Tool.Shape); Dispatcher.UIThread.RunJobs();
        Assert.Equal(22, Field("Corner radius").Value);
        Click(window, Eye("Fill")); Assert.False(session.ActiveLayer.Shape.FillEnabled);
        Screenshots.Save(window, "shape-properties-inline-shape");
        session.SelectLayer(session.Document.Layers[0].Id); Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), c => c.Name == "StrokeVisibility");
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
        var picker = dialog.GetVisualDescendants().OfType<TextBox>().Single(c=>c.Name=="PaintHex");
        picker.Focus(); picker.Text="0000FF"; dialog.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null); Dispatcher.UIThread.RunJobs();
        Assert.Equal((uint)SKColors.Blue, session.ActiveLayer!.Shape!.Fill);
        dialog.Close(false); Dispatcher.UIThread.RunJobs();
        Assert.Equal((uint)SKColors.Red, session.ActiveLayer!.Shape!.Fill);
        Assert.Same(pixels, session.ActiveLayer.Pixels); Assert.Equal("Rectangle", session.History.UndoName);
        Assert.False(session.IsInteracting);
        window.Close();
    }

    [AvaloniaFact]
    public void Vector_paint_modes_preview_commit_and_cancel_independently()
    {
        var window=new MainWindow { Width=1280,Height=800 }; window.Show();
        var session=EditorSession.NewCanvas(600,400,SKColors.White); window.AddSession(session);
        var layer=session.AddShape(new ShapeStyle(ShapeKind.Ellipse,0xffa04025,0) { Stroke=0xff254086,StrokeWidth=12 },new SKRect(100,50,480,300))!;
        window.SelectTool(Tool.Shape); Dispatcher.UIThread.RunJobs();
        void Open(string name) => Click(window,window.GetVisualDescendants().OfType<Border>().Single(b=>ToolTip.GetTip(b) as string==name+" color"));
        void Mode(Window dialog,string mode)
        {
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.Contains("Linear")).SelectedItem=mode;
            Dispatcher.UIThread.RunJobs();
        }
        Open("Fill"); var fill=window.OwnedWindows.Last(); Mode(fill,"Linear");
        Assert.False(session.ActiveLayer!.Shape!.FillGradient!.Radial);
        var strip = fill.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "RampPreview");
        Point StripAt(double fraction) => strip.TranslatePoint(new Point(7+(strip.Bounds.Width-14)*fraction,8),fill)!.Value;
        fill.MouseDown(StripAt(.36),MouseButton.Left);
        fill.MouseMove(StripAt(.64),RawInputModifiers.LeftMouseButton);
        fill.MouseUp(StripAt(.64),MouseButton.Left); Dispatcher.UIThread.RunJobs();
        var stops=session.ActiveLayer!.Shape!.FillGradient!.Ramp.Colors;
        Assert.Equal(3,stops.Length); Assert.InRange(stops[1].Position,.63,.65);
        Screenshots.Save(fill,"vector-paint-stops");
        Click(fill,fill.GetVisualDescendants().OfType<Button>().Single(b=>b.IsDefault));
        var accepted=session.ActiveLayer!.Shape;
        Open("Stroke"); var stroke=window.OwnedWindows.Last(); Mode(stroke,"Radial");
        Assert.True(session.ActiveLayer!.Shape!.StrokeGradient!.Radial);
        stroke.Close(false); Dispatcher.UIThread.RunJobs(); Assert.Equal(accepted,session.ActiveLayer!.Shape);
        session.Undo(); Assert.Null(session.ActiveLayer!.Shape!.FillGradient); window.Close();
    }

    [AvaloniaFact]
    public void Hex_rgb_cmyk_and_visibility_edit_the_same_paint_and_preserve_hidden_stroke()
    {
        var w=new MainWindow { Width=1280,Height=820 }; w.Show();
        var session=EditorSession.NewCanvas(500,400,SKColors.White); w.AddSession(session);
        var style=new ShapeStyle(ShapeKind.Rectangle,0xff204060,0) { Stroke=0xffaa3300, StrokeWidth=14, StrokeGradient=new(GradientRamp.Presets["Sunset"]) };
        session.AddShape(style,new SKRect(30,30,250,210)); w.SelectTool(Tool.Shape); Dispatcher.UIThread.RunJobs();
        TextBox Field(Window window,string name)=>window.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name==name);
        void Enter(Window window,string name,string value)
        {
            var box=Field(window,name); box.Focus();box.Text=value;
            window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);Dispatcher.UIThread.RunJobs();
        }
        var eye=w.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="StrokeVisibility");
        Click(w,eye);Assert.False(session.ActiveLayer!.Shape!.StrokeEnabled);
        Assert.Equal(style.StrokeGradient,session.ActiveLayer.Shape.StrokeGradient);
        Click(w,eye);Assert.Equal(style,session.ActiveLayer.Shape);
        Assert.Contains(Field(w,"FillHex").GetVisualDescendants(),v=>v is Avalonia.Controls.Presenters.TextPresenter);
        Assert.Equal("204060",Field(w,"FillHex").Text); Enter(w,"FillHex","#f00"); Enter(w,"FillOpacity","50");
        Assert.Equal(0x80ff0000u,session.ActiveLayer.Shape.Fill);
        Click(w,w.GetVisualDescendants().OfType<Border>().Single(b=>ToolTip.GetTip(b) as string=="Fill color"));
        var dialog=w.OwnedWindows.Last();Assert.Equal("255",Field(dialog,"PaintR").Text);
        var space=dialog.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="PaintChannels");
        space.SelectedItem="CMYK"; Dispatcher.UIThread.RunJobs();
        Assert.Equal("100",Field(dialog,"PaintM").Text);Enter(dialog,"PaintM","0");
        Assert.Equal("FFFF00",Field(dialog,"PaintHex").Text);
        Screenshots.Save(dialog,"vector-paint-cmyk");
        Click(dialog,dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.IsDefault));
        Assert.Equal(0x80ffff00u,session.ActiveLayer.Shape.Fill);
        Screenshots.Save(w,"vector-paint-toolbar"); w.Close();
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
        Assert.Equal(5, dialog.GetVisualDescendants().OfType<SliderField>().Count());
        Assert.Single(dialog.GetVisualDescendants().OfType<AngleDial>());
        var styles = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.OfType<string>().Contains("Radial")); styles.SelectedIndex = 1;
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

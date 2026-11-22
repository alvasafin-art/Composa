using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class AutoLayoutUiTests
{
    [AvaloniaFact]
    public void AutoLayout_preview_gap_drag_exact_entry_reorder_and_brush_in_real_window()
    {
        var app = Assert.IsType<App>(Application.Current);
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1280, Height = 720 };
        var s = EditorSession.NewCanvas(640, 420, new SKColor(232, 220, 202));
        var file = Environment.GetEnvironmentVariable("COMPOSA_LAYOUT_PREVIEW_PHOTO");
        using var source = File.Exists(file) ? ImageFiles.Load(file!) : null;
        var pixels = Pixels.NewColor(260, 174);
        using (var canvas = new SKCanvas(pixels))
        {
            if (source != null) canvas.DrawBitmap(source, new SKRect(0, 0, pixels.Width, pixels.Height));
            else canvas.Clear(new SKColor(133, 93, 67));
        }
        try
        {
            app.ApplyTheme(EditorTheme.DarkNeutral);
            window.Show();
            window.AddSession(s);
            var photo = s.AddImageLayer("Фотография", pixels, new(320, 118), fit: false);
            var title = s.AddText(new(192, 230), new TextStyle { Text = "Дрип-кофе", Size = 32, Color = 0xff302219 });
            var price = s.AddText(new(192, 305), new TextStyle { Text = "690 ₽", Size = 21, Color = 0xff674627 });
            window.SelectTool(Tool.Move);
            s.SelectLayer(photo.Id);
            s.SelectLayer(title.Id, extend: true);
            s.SelectLayer(price.Id, extend: true);
            Pump();

            Click("CreateAutoLayout");
            Assert.False(s.ActiveLayer!.IsGroup);
            Click("CancelAutoLayout");
            Assert.False(s.ActiveLayer!.IsGroup);
            Click("CreateAutoLayout");
            Click("ApplyAutoLayout");
            var group = Assert.IsType<Layer>(s.ActiveLayer);
            Assert.Equal(LayoutDirection.Column, group.AutoLayout!.Direction);
            var field = Find<NumericUpDown>("AutoLayoutGap");
            field.Value = 20;
            Assert.Equal(20, group.AutoLayout.Gap);
            var gap = Gap();
            window.MouseMove(gap);
            window.MouseDown(gap, MouseButton.Left);
            Assert.True(window.Canvas.IsDragging);
            window.MouseUp(gap, MouseButton.Left);
            Pump();
            var editor = Find<TextBox>("CanvasLayoutGapInput");
            Assert.True(editor.IsFocused);
            window.KeyTextInput("12");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            Assert.Equal(12, group.AutoLayout.Gap);
            Assert.Equal(12m, field.Value);
            window.MouseMove(Gap());
            Assert.True(Screenshots.Save(window, "auto-layout-dark-1280"));
            app.ApplyTheme(EditorTheme.LightNeutral);
            Assert.True(Screenshots.Save(window, "auto-layout-light-1280"));

            var before = s.History.CurrentId;
            gap = Gap();
            var end = gap + new Vector(0, 14);
            window.MouseDown(gap, MouseButton.Left);
            window.MouseMove(gap + new Vector(0, 7), RawInputModifiers.LeftMouseButton);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Assert.True(Screenshots.Save(window, "auto-layout-gap-drag"));
            window.MouseUp(end, MouseButton.Left);
            Assert.True(group.AutoLayout.Gap > 12);
            s.Undo();
            Assert.Equal(before, s.History.CurrentId);
            group = s.Document.Find(group.Id)!;
            Assert.Equal(12, group.AutoLayout!.Gap);

            var last = s.LayoutElements(group)[^1].Bounds;
            var first = s.LayoutElements(group)[0].Bounds;
            var drag = At(last.MidX, last.MidY);
            var drop = At(first.MidX, first.Top + 5);
            window.MouseDown(drag, MouseButton.Left);
            window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
            Assert.True(Screenshots.Save(window, "auto-layout-insertion-line"));
            window.MouseUp(drop, MouseButton.Left);
            Assert.Equal(price.Id, s.LayoutElements(group)[0].Layer.Id);
            s.SelectLayer(photo.Id);
            Pump();
            Assert.NotNull(Find<ComboBox>("AutoLayoutWidthMode"));
            Assert.True(Screenshots.Save(window, "auto-layout-child-1280"));
            window.SelectTool(Tool.Brush);
            Pump();
            Assert.DoesNotContain(window.GetVisualDescendants(), control => control is ComboBox { Name: "AutoLayoutWidthMode" });
            var frame = EditorSession.LayoutBounds(s.ActiveLayer!);
            window.MouseMove(At(frame.MidX, frame.MidY));
            Assert.True(Screenshots.Save(window, "auto-layout-brush-1280"));
            window.Width = 1920;
            window.Height = 1080;
            window.SelectTool(Tool.Move);
            s.Undo();
            s.SelectLayer(group.Id);
            window.UpdateLayout();
            window.MouseMove(Gap());
            Assert.True(Screenshots.Save(window, "auto-layout-light-1920"));
        }
        finally
        {
            Locale.Language = "en";
            app.ApplyTheme(EditorTheme.Classic);
            s.MarkSaved("auto-layout.cmps", s.History.CurrentId);
            window.Close();
        }

        Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
        Point Gap()
        {
            var elements = s.LayoutElements(s.ActiveLayer!);
            return At(elements[0].Bounds.MidX, (elements[0].Bounds.Bottom + elements[1].Bounds.Top) / 2);
        }
        T Find<T>(string name) where T : Control => Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);
        void Click(string name)
        {
            Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        static void Pump() => Dispatcher.UIThread.RunJobs();
    }
}

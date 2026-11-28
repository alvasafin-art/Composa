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
            var gap = GapLabel();
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
            gap = GapLabel();
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
            s.SelectLayer(price.Id);
            window.MouseDown(drag, MouseButton.Left, RawInputModifiers.Control);
            window.MouseMove(drop, RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
            Assert.True(Screenshots.Save(window, "auto-layout-insertion-line"));
            window.MouseUp(drop, MouseButton.Left, RawInputModifiers.Control);
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
        Point GapLabel()
        {
            window.MouseMove(Gap());
            var position = window.Canvas.ToScreen(new SKPoint(EditorSession.LayoutBounds(s.ActiveLayer!).Right, 0)).X;
            var band = Gap();
            var x = window.Canvas.TranslatePoint(new Point(position + 20, 0), window)!.Value.X;
            return new Point(x, band.Y);
        }
        T Find<T>(string name) where T : Control => Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);
        void Click(string name)
        {
            Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        static void Pump() => Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dragging_content_moves_the_whole_card_and_releases_at_the_pointer_without_reflow()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        var s = EditorSession.NewCanvas(700, 530, SKColors.White);
        try
        {
            window.Show();
            window.AddSession(s);
            window.SelectTool(Tool.Move);
            window.Canvas.AutoSelect = true;
            window.Canvas.ShowTransformControls = false;
            s.View = s.View with { Snap = false };
            using var bitmap = Pixels.NewColor(230, 130);
            bitmap.Erase(SKColors.Peru);
            var photo = s.AddImageLayer("Photo", bitmap, new(160, 110), fit: false);
            var heading = s.AddText(new(40, 200), new TextStyle { Text = "Coffee from the highlands", Size = 25 });
            var price = s.AddText(new(40, 250), new TextStyle { Text = "690 ₽", Size = 22 });
            s.SelectLayer(photo.Id);
            s.SelectLayer(heading.Id, extend: true);
            s.SelectLayer(price.Id, extend: true);
            Assert.True(s.CreateAutoLayout(s.ProposeAutoLayout(out _)!));
            var group = s.ActiveLayer!;
            s.SetAutoLayout(group, group.AutoLayout! with { Gap = 12, Padding = new(8, 10, 12, 14) });
            s.SetAutoLayoutItem(photo, new(LayoutSizeMode.Fixed, LayoutSizeMode.Fixed, 190, 88, LayoutImageFit.Crop));
            s.AddMask(photo);
            s.SetMaskLinked(photo, false);
            s.ClearLayerSelection();
            Dispatcher.UIThread.RunJobs();

            var revision = s.Revision;
            var groupBounds = EditorSession.LayoutBounds(group);
            var photoFrame = photo.LayoutFrame!;
            var headingTransform = heading.Transform;
            var mask = photo.MaskMatrix;
            var point = new SKPoint(photoFrame.Bounds.MidX, photoFrame.Bounds.MidY);
            Point At(float dx, float dy) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new(point.X + dx, point.Y + dy)), window)!.Value;
            window.MouseMove(At(0, 0));
            window.MouseDown(At(0, 0), MouseButton.Left);
            window.MouseMove(At(74, 52), RawInputModifiers.LeftMouseButton);
            Assert.Equal(group.Id, s.ActiveLayer?.Id);
            Assert.Equal(photoFrame.X + 74, photo.LayoutFrame!.X, 2);
            Assert.Equal(headingTransform.Y + 52, heading.Transform.Y, 2);
            Assert.Equal(groupBounds.Left + 74, EditorSession.LayoutBounds(group).Left, 2);
            Assert.Equal(revision, s.Revision);

            window.MouseUp(At(92, 63), MouseButton.Left); // The release can land past the last delivered motion.
            Assert.Equal(revision + 1, s.Revision);
            Assert.Equal(groupBounds.Left + 92, EditorSession.LayoutBounds(group).Left, 2);
            Assert.Equal(groupBounds.Top + 63, EditorSession.LayoutBounds(group).Top, 2);
            Assert.Equal(photoFrame with { X = photoFrame.X + 92, Y = photoFrame.Y + 63 }, photo.LayoutFrame);
            Assert.Equal(mask.TransX + 92, photo.MaskMatrix.TransX, 2);
            Assert.Equal(headingTransform.Y + 63, heading.Transform.Y, 2);
            Assert.Equal(12, group.AutoLayout!.Gap);
            Assert.Equal(new[] { photo.Id, heading.Id, price.Id }, s.LayoutElements(group).Select(unit => unit.Layer.Id));

            s.Undo();
            Assert.Equal(groupBounds, EditorSession.LayoutBounds(s.Document.Find(group.Id)!));
            Assert.Equal(photoFrame, s.Document.Find(photo.Id)!.LayoutFrame);
            s.Redo();
            s.SelectLayer(group.Id);
            window.MouseDown(At(92, 63), MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(At(92, 63), MouseButton.Left, RawInputModifiers.Control);
            Assert.Equal(photo.Id, s.ActiveLayer?.Id);
        }
        finally { s.MarkSaved("layout-move.cmps"); window.Close(); }
    }
}

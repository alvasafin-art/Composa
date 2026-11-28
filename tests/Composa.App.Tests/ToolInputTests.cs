using Avalonia;
using Composa.App.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Every canvas tool driven through real pointer and key events.</summary>
public class ToolInputTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public ToolInputTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    [AvaloniaFact]
    public void Alt_switches_the_brush_to_an_eyedropper_without_moving_the_pointer()
    {
        window.SelectTool(Tool.Brush);
        window.MouseMove(At(300, 200));
        var brushCursor = window.Canvas.Cursor;
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        var eyedropper = window.Canvas.Cursor;
        Assert.NotSame(brushCursor, eyedropper);
        Assert.NotEqual("Cross", eyedropper?.ToString());
        Click(300, 200, RawInputModifiers.Alt);
        Assert.Same(eyedropper, window.Canvas.Cursor);
        Assert.Equal(SKColors.White, (SKColor)session.Foreground);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.Alt);
        Assert.Same(brushCursor, window.Canvas.Cursor);
        Assert.Equal(Tool.Brush, session.Tool);
    }

    private void Drag(SKPoint from, SKPoint to, RawInputModifiers modifiers = RawInputModifiers.None, int steps = 6)
    {
        window.MouseDown(At(from.X, from.Y), MouseButton.Left, modifiers);
        for (var i = 1; i <= steps; i++)
            window.MouseMove(At(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps), modifiers | RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(to.X, to.Y), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private void Click(float x, float y, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(At(x, y), MouseButton.Left, modifiers);
        window.MouseUp(At(x, y), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Selection_brush_diameter_is_drawn_during_a_stroke_at_the_current_zoom()
    {
        window.SelectTool(Tool.SelectionBrush);
        session.SelectionBrushSize = 80; session.SelectionBrushFeather = 10;
        window.Canvas.ZoomTo(.5); Dispatcher.UIThread.RunJobs();
        window.MouseMove(At(200,150));
        window.MouseDown(At(200,150),MouseButton.Left);
        window.MouseMove(At(250,150),RawInputModifiers.LeftMouseButton);
        Assert.True(window.Canvas.IsDragging);
        Dispatcher.UIThread.RunJobs();
        var overlay = window.Canvas.Child!;
        using var frame = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)overlay.Bounds.Width, (int)overlay.Bounds.Height));
        frame.Render(overlay);
        using var data = new MemoryStream(); frame.Save(data); data.Position = 0;
        using var bitmap = SKBitmap.Decode(data);
        var center = window.Canvas.ToScreen(new SKPoint(250, 150));
        // At half zoom an 80 px brush has a 20 px radius plus its contrast ring.
        bool Ring(int dx, int dy) => bitmap.GetPixel((int)Math.Round(center.X) + dx, (int)Math.Round(center.Y) + dy).Alpha > 40;
        Assert.True(Ring(20, 0) || Ring(21, 0));
        Assert.True(Ring(0, 20) || Ring(0, 21));
        Assert.False(Ring(30, 0));
        Screenshots.Save(window,"selection-brush-diameter-during-stroke");
        window.MouseUp(At(250,150),MouseButton.Left); Assert.False(window.Canvas.IsDragging);
    }

    [AvaloniaTheory]
    [InlineData(Tool.Brush)]
    [InlineData(Tool.Eraser)]
    [InlineData(Tool.HealingBrush)]
    [InlineData(Tool.SpotHealing)]
    public void Large_brush_outline_follows_hover_and_clears_its_previous_position(Tool tool)
    {
        window.SelectTool(tool);
        session.Brush = session.Brush with { Size = 180, Hardness = .4, FullCursor = false };
        window.Canvas.ZoomTo(1);
        Dispatcher.UIThread.RunJobs();
        var before = At(140, 205);
        var after = At(420, 205);
        var revision = session.Revision;
        window.MouseMove(before);
        Assert.True(Screenshots.Save(window, "large-brush-outline-before"));
        window.MouseMove(after);
        Assert.True(Screenshots.Save(window, "large-brush-outline-after"));
        using var first = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "large-brush-outline-before.png"));
        using var second = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "large-brush-outline-after.png"));
        Assert.True(HasRing(first, before, 63));
        Assert.False(HasRing(second, before, 63));
        Assert.True(HasRing(second, after, 63));
        Assert.False(HasRing(second, after, 90));
        session.Brush = session.Brush with { FullCursor = true };
        window.Canvas.InvalidateBrushCursor();
        Assert.True(Screenshots.Save(window, "large-brush-outline-full"));
        using var full = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "large-brush-outline-full.png"));
        Assert.True(HasRing(full, after, 90));
        Assert.False(HasRing(full, after, 63));
        Assert.Equal(revision, session.Revision);

        static bool HasRing(SKBitmap bitmap, Point center, int radius) => Enumerable.Range(radius - 2, 5)
            .Any(dx => bitmap.GetPixel((int)Math.Round(center.X) + dx, (int)Math.Round(center.Y)).Red < 200);
    }

    [AvaloniaFact]
    public void Fast_brush_release_extends_the_existing_stroke()
    {
        window.SelectTool(Tool.Brush);
        session.Foreground = SKColors.Black;
        session.Brush = session.Brush with { Size = 24 };
        var revision = session.Revision;
        window.MouseDown(At(100, 150), MouseButton.Left);
        window.MouseMove(At(120, 150), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(450, 150), MouseButton.Left);
        Assert.False(session.IsStroking);
        Assert.Equal(revision + 1, session.Revision);
        Assert.Equal(SKColors.Black, session.ActiveLayer!.Pixels!.GetPixel(449, 150));
        Assert.Equal(SKColors.White, session.ActiveLayer.Pixels.GetPixel(470, 150));
        session.Undo();
        Assert.Equal(SKColors.White, session.ActiveLayer!.Pixels!.GetPixel(449, 150));
    }

    [AvaloniaFact]
    public void Marquee_selects_adds_subtracts_and_moves()
    {
        window.SelectTool(Tool.Marquee);
        Drag(new SKPoint(100, 100), new SKPoint(200, 180));
        Assert.Equal(new SKRectI(100, 100, 200, 180), SelectionMask.Bounds(session.Selection!));
        Drag(new SKPoint(300, 100), new SKPoint(350, 150), RawInputModifiers.Shift);
        Assert.Equal(new SKRectI(100, 100, 350, 180), SelectionMask.Bounds(session.Selection!));
        Drag(new SKPoint(290, 90), new SKPoint(360, 160), RawInputModifiers.Alt);
        Assert.Equal(new SKRectI(100, 100, 200, 180), SelectionMask.Bounds(session.Selection!));
        Drag(new SKPoint(150, 150), new SKPoint(170, 160)); // Inside: moves the outline.
        Assert.Equal(new SKRectI(120, 110, 220, 190), SelectionMask.Bounds(session.Selection!));
        Click(500, 350);
        Assert.Null(session.Selection);
    }

    [AvaloniaFact]
    public void Marquees_shapes_and_selection_moves_snap_to_the_targets_unless_ctrl_is_held()
    {
        session.AddGuide(GuideAxis.Vertical, 250);
        session.AddGuide(GuideAxis.Vertical, 300);
        window.SelectTool(Tool.Marquee);
        // The dragged corner lands 3 pixels short of the guide and is pulled onto it.
        Drag(new SKPoint(100, 100), new SKPoint(247, 180));
        Assert.Equal(new SKRectI(100, 100, 250, 180), SelectionMask.Bounds(session.Selection!));
        // The outline moved by 47 brings its right edge within reach of the second guide; Shift keeps the move on one axis.
        Drag(new SKPoint(150, 150), new SKPoint(197, 152));
        Assert.Equal(new SKRectI(150, 102, 300, 182), SelectionMask.Bounds(session.Selection!));
        window.MouseDown(At(200, 150), MouseButton.Left); // Shift at the press would add a marquee instead; it locks the axis once the drag is under way.
        window.MouseMove(At(190, 148), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        window.MouseMove(At(180, 145), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        window.MouseUp(At(180, 145), MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRectI(130, 102, 280, 182), SelectionMask.Bounds(session.Selection!));
        // Ctrl pressed during the drag (at the press it would move pixels or the layer) draws the marquee where the pointer is.
        session.Deselect();
        window.MouseDown(At(100, 100), MouseButton.Left);
        window.MouseMove(At(200, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
        window.MouseMove(At(247, 180), RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
        window.MouseUp(At(247, 180), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRectI(100, 100, 247, 180), SelectionMask.Bounds(session.Selection!));
        session.Deselect();

        // A shape's corners snap the same way: the start onto the canvas edge, the far corner onto the guide.
        window.SelectTool(Tool.Shape);
        session.ShapeKind = ShapeKind.Rectangle;
        Drag(new SKPoint(3, 50), new SKPoint(247, 120));
        var shape = Assert.Single(session.Document.Layers, l => l.Shape != null);
        Assert.Equal((0d, 50d, 250d, 70d), (shape.Transform.X, shape.Transform.Y, shape.Transform.Width, shape.Transform.Height));
        session.View = session.View with { Snap = false };
        Drag(new SKPoint(300, 50), new SKPoint(347, 120));
        var free = session.Document.Layers.Last(l => l.Shape != null);
        Assert.Equal((300d, 47d), (free.Transform.X, free.Transform.Width));
    }

    [AvaloniaFact]
    public void Shift_squares_a_marquee_unless_it_chose_the_add_mode()
    {
        window.SelectTool(Tool.Marquee);
        // With nothing to add to, a Shift held from the start squares the marquee.
        Drag(new SKPoint(100, 100), new SKPoint(180, 140), RawInputModifiers.Shift);
        Assert.Equal(new SKRectI(100, 100, 180, 180), SelectionMask.Bounds(session.Selection!));
        // With a selection, a Shift held before the press adds to it and the marquee keeps the shape it is dragged in.
        Drag(new SKPoint(300, 100), new SKPoint(360, 140), RawInputModifiers.Shift);
        Assert.Equal(new SKRectI(100, 100, 360, 180), SelectionMask.Bounds(session.Selection!));
        // Letting Shift go mid-drag and pressing it again squares the marquee while it still adds.
        window.MouseDown(At(400, 300), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(At(430, 320), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(460, 330), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        window.MouseUp(At(460, 330), MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRectI(100, 100, 460, 360), SelectionMask.Bounds(session.Selection!));
    }

    [AvaloniaFact]
    public void Lasso_wand_and_polygon_select()
    {
        window.SelectTool(Tool.Lasso);
        window.MouseDown(At(50, 50), MouseButton.Left);
        foreach (var p in new[] { new SKPoint(150, 50), new SKPoint(150, 150), new SKPoint(50, 150) }) window.MouseMove(At(p.X, p.Y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(50, 150), MouseButton.Left);
        Assert.NotNull(session.Selection);
        Assert.Equal(255, session.Selection!.GetPixel(100, 100).Alpha);

        session.LassoKind = LassoKind.Polygonal;
        window.SelectTool(Tool.Lasso);
        Click(300, 50); Click(400, 50); Click(400, 150);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(255, session.Selection!.GetPixel(380, 70).Alpha);
        Assert.Equal(0, session.Selection!.GetPixel(100, 100).Alpha);

        session.Deselect();
        window.SelectTool(Tool.Wand);
        Click(10, 10);
        Assert.Equal(new SKRectI(0, 0, 600, 400), SelectionMask.Bounds(session.Selection!, 128));
    }

    [AvaloniaFact]
    public void Gradient_shape_eyedropper_and_fill_keys()
    {
        session.Foreground = SKColors.Red;
        session.Background = SKColors.Blue;
        window.SelectTool(Tool.Gradient);
        Drag(new SKPoint(0, 200), new SKPoint(600, 200));
        Assert.True(session.IsInteracting); // Still adjustable.
        Assert.True(session.Composite().GetPixel(300, 200).Red is > 100 and < 160);
        Drag(new SKPoint(600, 200), new SKPoint(300, 200)); // Pull the end in: everything right of it is now pure blue.
        Assert.True(session.Composite().GetPixel(320, 200).Blue > 240);
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); // Undo takes the open gradient back.
        Assert.False(session.CanUndo);
        Assert.Equal(255, session.Composite().GetPixel(20, 200).Green);
        Drag(new SKPoint(0, 200), new SKPoint(600, 200));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.False(session.IsInteracting);
        Assert.Equal("Gradient", session.History.UndoName);
        Assert.True(session.Composite().GetPixel(20, 200).Red > 200);
        Assert.True(session.Composite().GetPixel(580, 200).Blue > 200);

        window.SelectTool(Tool.Shape);
        session.Foreground = SKColors.Lime;
        Drag(new SKPoint(100, 100), new SKPoint(200, 160));
        var shape = Assert.Single(session.Document.Layers, l => l.Shape != null);
        Assert.Equal((100d, 100d, 100d, 60d), (shape.Transform.X, shape.Transform.Y, shape.Transform.Width, shape.Transform.Height));

        window.SelectTool(Tool.Eyedropper);
        Click(150, 130);
        Assert.Equal(SKColors.Lime, session.Foreground);

        session.SelectLayer(session.Document.Layers[0].Id);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.Control);
        Assert.Equal("Fill", session.History.UndoName);
        TestColor(SKColors.Blue, session.Document.Layers[0].Pixels!.GetPixel(5, 5));
    }

    [AvaloniaFact]
    public void Move_tool_drags_scales_with_handles_and_nudges()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        Drag(new SKPoint(300, 200), new SKPoint(340, 230));
        Assert.Equal((290d, 180d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal("Move", session.History.UndoName);

        Drag(new SKPoint(390, 280), new SKPoint(440, 330)); // Bottom-right handle, proportional.
        Assert.Equal((150d, 150d), (Math.Round(layer.Transform.Width), Math.Round(layer.Transform.Height)));
        Assert.Equal(290, layer.Transform.X);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Assert.Equal(300, layer.Transform.X);

        // A key pressed mid-drag must not start another edit.
        window.MouseDown(At(350, 250), MouseButton.Left);
        window.MouseMove(At(360, 250), RawInputModifiers.LeftMouseButton);
        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        window.MouseMove(At(380, 250), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(380, 250), MouseButton.Left);
        Assert.Equal(Tool.Move, session.Tool);
        Assert.Equal(330, layer.Transform.X);
        session.Undo();
        Assert.Equal(300, session.Document.Find(layer.Id)!.Transform.X);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Alt_dragging_an_edge_scales_proportionally_about_the_center(bool topEdge)
    {
        var pixels = Rendering.Pixels.NewColor(100, 60);
        pixels.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", pixels, new SKPoint(300, 200));
        session.View = session.View with { Snap = false };
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();

        var start = topEdge ? new SKPoint(300, 170) : new SKPoint(350, 200);
        var end = topEdge ? new SKPoint(310, 158) : new SKPoint(370, 210);
        Drag(start, end, RawInputModifiers.Alt);

        AssertNear(new SKPoint(300, 200), layer.Transform.Center);
        AssertNear(new SKPoint(140, 84), new SKPoint((float)layer.Transform.Width, (float)layer.Transform.Height));
        Assert.Null(layer.Transform.Distort);
        Assert.Equal("Scale", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Shift_and_alt_during_corner_distortion_mirror_the_other_end_of_the_edge()
    {
        var pixels = Rendering.Pixels.NewColor(100, 100);
        pixels.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", pixels, new SKPoint(300, 200));
        session.View = session.View with { Snap = false };
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();

        const RawInputModifiers control = RawInputModifiers.Control;
        const RawInputModifiers shifted = control | RawInputModifiers.Shift;
        const RawInputModifiers perspective = shifted | RawInputModifiers.Alt;
        var original = layer.ControlTransform.FrameCorners();
        var end = At(270, 162);
        window.MouseDown(At(250, 150), MouseButton.Left, control);
        window.MouseMove(end, control | RawInputModifiers.LeftMouseButton);
        AssertNear(new SKPoint(270, 162), layer.ControlTransform.FrameCorners()[0]);
        AssertNear(original[1], layer.ControlTransform.FrameCorners()[1]);

        window.KeyPressQwerty(PhysicalKey.ShiftLeft, control);
        window.KeyPressQwerty(PhysicalKey.AltLeft, shifted);
        var mirrored = layer.ControlTransform.FrameCorners();
        AssertNear(new SKPoint(270, 150), mirrored[0]);
        AssertNear(new SKPoint(330, 150), mirrored[1]);
        AssertNear(original[2], mirrored[2]);
        AssertNear(original[3], mirrored[3]);

        window.KeyReleaseQwerty(PhysicalKey.AltLeft, perspective);
        AssertNear(new SKPoint(270, 162), layer.ControlTransform.FrameCorners()[0]);
        AssertNear(original[1], layer.ControlTransform.FrameCorners()[1]);
        window.KeyPressQwerty(PhysicalKey.AltLeft, shifted);
        window.MouseUp(end, MouseButton.Left, perspective);
        Assert.Equal(mirrored, layer.ControlTransform.FrameCorners());
        Assert.Equal("Distort", session.History.UndoName);
        session.Undo();
        Assert.Equal(original, session.ActiveLayer!.ControlTransform.FrameCorners());
    }

    [AvaloniaFact]
    public void Shift_alt_on_an_already_distorted_corner_mirrors_a_vertical_drag()
    {
        var pixels = Rendering.Pixels.NewColor(100, 100);
        pixels.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", pixels, new SKPoint(300, 200));
        layer.Transform = layer.Transform with { Distort = [0, 0, 0, 0, 10, 0, 0, 0] };
        session.View = session.View with { Snap = false };
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        var original = layer.ControlTransform.FrameCorners();
        var offset = new SKPoint(0, 20);

        Drag(original[0], original[0] + offset, RawInputModifiers.Shift | RawInputModifiers.Alt);

        var corners = layer.ControlTransform.FrameCorners();
        AssertNear(original[0] + offset, corners[0]);
        AssertNear(original[1], corners[1]);
        AssertNear(original[2], corners[2]);
        AssertNear(original[3] - offset, corners[3]);
    }

    [AvaloniaFact]
    public void Distorting_a_corner_moves_its_handle_with_it_and_the_corner_keeps_distorting_afterwards()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200)); // 250..350 × 150..250
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();

        Drag(new SKPoint(250, 150), new SKPoint(250, 180), RawInputModifiers.Control);
        Assert.Equal("Distort", session.History.UndoName);
        Assert.Equal((250d, 150d, 100d, 100d), (layer.Transform.X, layer.Transform.Y, layer.Transform.Width, layer.Transform.Height));
        Assert.Equal([0f, 30f, 0f, 0f, 0f, 0f, 0f, 0f], layer.Transform.Distort);
        var corners = layer.Transform.Corners(100, 100);
        AssertNear(new SKPoint(250, 180), corners[0]);

        // A plain drag on the moved handle goes on distorting rather than scaling the rectangle.
        Drag(new SKPoint(250, 180), new SKPoint(270, 180));
        Assert.Equal([20f, 30f, 0f, 0f, 0f, 0f, 0f, 0f], layer.Transform.Distort);
        Assert.Equal((100d, 100d), (layer.Transform.Width, layer.Transform.Height));

        // Every corner of a distorted layer distorts; the edge handles still scale the whole shape.
        Drag(new SKPoint(350, 250), new SKPoint(400, 280));
        Assert.Equal("Distort", session.History.UndoName);
        Assert.Equal([20f, 30f, 0f, 0f, 50f, 30f, 0f, 0f], layer.Transform.Distort);
        Drag(new SKPoint(375, 215), new SKPoint(425, 215)); // The right edge's midpoint, between the top-right and the moved bottom-right corner.
        Assert.Equal("Scale", session.History.UndoName);
        Assert.True(layer.Transform.Width > 100);
        Assert.Equal(20 * layer.Transform.Width / 100, layer.Transform.Distort![0], 1); // The distortion scales with the frame.
    }

    [AvaloniaFact]
    public void Ctrl_drag_on_each_edge_moves_its_two_corners_and_undo_restores_the_frame()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        session.View = session.View with { Snap = false };
        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
        var original = layer.Transform.FrameCorners();
        var offset = new SKPoint(24, -16);

        for (var first = 0; first < 4; first++)
        {
            var second = (first + 1) % 4;
            var mid = Midpoint(original[first], original[second]);
            Drag(mid, mid + offset, RawInputModifiers.Control);
            var transformed = session.ActiveLayer!.Transform.FrameCorners();
            for (var i = 0; i < 4; i++)
                AssertNear(original[i] + (i == first || i == second ? offset : SKPoint.Empty), transformed[i]);
            Assert.Equal("Distort", session.History.UndoName);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.Equal(original, session.ActiveLayer!.Transform.FrameCorners());
        }
    }

    [AvaloniaFact]
    public void Distorting_a_rotated_edge_can_skew_along_the_edge_and_cancel_with_ctrl_held()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        layer.Transform = layer.Transform with { Rotation = 30, FlipHorizontal = true };
        session.View = session.View with { Snap = false };
        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
        var original = layer.Transform.FrameCorners();
        var mid = Midpoint(original[0], original[1]);
        var delta = new SKPoint(30, 10);
        var end = mid + delta;
        var edge = original[1] - original[0];
        var fraction = (delta.X * edge.X + delta.Y * edge.Y) / (edge.X * edge.X + edge.Y * edge.Y);
        var expected = new SKPoint(edge.X * fraction, edge.Y * fraction);

        window.MouseDown(At(mid.X, mid.Y), MouseButton.Left, RawInputModifiers.Control);
        window.MouseMove(At(end.X, end.Y), RawInputModifiers.Control | RawInputModifiers.LeftMouseButton);
        AssertNear(original[0] + delta, layer.Transform.FrameCorners()[0]);
        window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Control);
        AssertNear(original[0] + expected, layer.Transform.FrameCorners()[0]);
        AssertNear(original[1] + expected, layer.Transform.FrameCorners()[1]);
        AssertNear(original[2], layer.Transform.FrameCorners()[2]);
        var previousStep = session.History.UndoName;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.Control);
        window.MouseUp(At(end.X, end.Y), MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(original, session.ActiveLayer!.Transform.FrameCorners());
        Assert.Equal(previousStep, session.History.UndoName);
    }

    [AvaloniaFact]
    public void Distorted_edge_snaps_its_midpoint_and_can_move_from_the_center()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        session.SetCanvasLayout(new CanvasLayout { SquareSize = 50 });
        session.View = session.View with { Snap = true, SnapToCanvasLayout = true };
        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
        var original = layer.Transform.FrameCorners();
        Drag(new SKPoint(300, 150), new SKPoint(348, 102), RawInputModifiers.Control | RawInputModifiers.Alt);
        var corners = layer.Transform.FrameCorners();
        AssertNear(new SKPoint(350, 100), Midpoint(corners[0], corners[1]));
        AssertNear(new SKPoint(250, 300), Midpoint(corners[2], corners[3]));
        AssertNear(original[0] + new SKPoint(50, -50), corners[0]);
    }

    private static SKPoint Midpoint(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static void AssertNear(SKPoint expected, SKPoint actual) =>
        Assert.True(SKPoint.Distance(expected, actual) < .05f, $"Expected {expected}, got {actual}");

    [AvaloniaFact]
    public void Auto_select_picks_a_layer_stacked_on_a_selected_background_and_can_be_turned_off()
    {
        var background = session.ActiveLayer!; // The white canvas covers every press.
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        session.SelectLayer(background.Id);
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();

        Click(320, 220);
        Assert.Equal(layer.Id, session.ActiveLayer!.Id);
        Click(20, 20);
        Assert.Equal(background.Id, session.ActiveLayer!.Id);
        Click(320, 220);
        Assert.Equal(layer.Id, session.ActiveLayer!.Id);

        // Off, a press keeps the current layer wherever it lands; Ctrl-click still picks. (Each click lands somewhere
        // new, because two at one spot make a double-click, which always picks.)
        window.Canvas.AutoSelect = false;
        session.SelectLayer(background.Id);
        Click(360, 260);
        Assert.Equal(background.Id, session.ActiveLayer!.Id);
        Click(340, 240, RawInputModifiers.Control);
        Assert.Equal(layer.Id, session.ActiveLayer!.Id);
    }

    [AvaloniaFact]
    public void Ctrl_drag_moves_the_layer_with_any_tool_and_leaves_the_other_ctrl_drags_alone()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        var blue = Rendering.Pixels.NewColor(40, 40);
        blue.Erase(SKColors.Blue);
        var other = session.AddImageLayer("other", blue, new SKPoint(500, 100));
        session.SelectLayer(layer.Id);
        window.SelectTool(Tool.Brush);
        session.Brush = session.Brush with { Size = 10, Hardness = 1 };
        Dispatcher.UIThread.RunJobs();

        // Pressing Ctrl over the canvas promises a move with the four-way arrow at once, before the pointer moves;
        // letting go brings the brush cursor back.
        window.MouseMove(At(300, 200), RawInputModifiers.None);
        Assert.Equal("None", window.Canvas.Cursor?.ToString());
        window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
        Assert.Equal("SizeAll", window.Canvas.Cursor?.ToString());
        window.KeyReleaseQwerty(PhysicalKey.ControlLeft, RawInputModifiers.Control);
        Assert.Equal("None", window.Canvas.Cursor?.ToString());
        window.MouseMove(At(300, 200), RawInputModifiers.Control);
        Assert.Equal("SizeAll", window.Canvas.Cursor?.ToString());
        window.MouseMove(At(302, 200), RawInputModifiers.None);
        Assert.Equal("None", window.Canvas.Cursor?.ToString());

        // With the Brush: the layer moves, nothing is painted, and the tool stays the Brush.
        Drag(new SKPoint(300, 200), new SKPoint(340, 230), RawInputModifiers.Control);
        Assert.Equal((290d, 180d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal("Move", session.History.UndoName);
        Assert.Equal(Tool.Brush, session.Tool);
        Assert.Equal(SKColors.Red, session.Composite().GetPixel(320, 220));

        // A Ctrl-press on another layer's pixels, away from the current frame, moves that layer instead.
        Drag(new SKPoint(500, 100), new SKPoint(510, 120), RawInputModifiers.Control);
        Assert.Equal((490d, 100d), (other.Transform.X, other.Transform.Y));
        Assert.Equal((290d, 180d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal(other.Id, session.ActiveLayer!.Id);

        // The Marquee's own Ctrl-drag inside a selection still moves the selected pixels rather than the layer.
        session.SelectLayer(layer.Id);
        window.SelectTool(Tool.Marquee);
        Dispatcher.UIThread.RunJobs();
        Drag(new SKPoint(300, 190), new SKPoint(340, 230));
        Drag(new SKPoint(320, 210), new SKPoint(320, 260), RawInputModifiers.Control);
        Assert.Equal(SKColors.Red, session.Composite().GetPixel(320, 270));  // The pixels moved down...
        Assert.Equal(SKColors.White, session.Composite().GetPixel(320, 200)); // ...leaving the white background behind.
        Assert.Equal((490d, 100d), (other.Transform.X, other.Transform.Y)); // No layer was moved.

        // Without a selection, a Ctrl-drag moves the layer again, snapping like an ordinary move does.
        session.ClearSelection();
        session.SelectLayer(other.Id);
        Drag(new SKPoint(510, 120), new SKPoint(23, 120), RawInputModifiers.Control);
        Assert.Equal(0, other.Transform.X);                                  // 3 px short, snapped onto the canvas edge.

        // The Type tool shows the move cursor too, instead of its I-beam, for as long as Ctrl is down.
        window.SelectTool(Tool.Text);
        window.MouseMove(At(200, 300), RawInputModifiers.None);
        Assert.Equal("Ibeam", window.Canvas.Cursor?.ToString());
        window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
        window.MouseMove(At(202, 300), RawInputModifiers.Control);
        Assert.Equal("SizeAll", window.Canvas.Cursor?.ToString());
        window.KeyReleaseQwerty(PhysicalKey.ControlLeft, RawInputModifiers.Control);
        window.MouseMove(At(204, 300), RawInputModifiers.None);
        Assert.Equal("Ibeam", window.Canvas.Cursor?.ToString());
    }

    [AvaloniaFact]
    public void Zoom_pan_and_escape()
    {
        window.SelectTool(Tool.Zoom);
        var before = window.Canvas.Zoom;
        Click(300, 200);
        Assert.True(window.Canvas.Zoom > before);
        Click(300, 200, RawInputModifiers.Alt);
        Assert.Equal(before, window.Canvas.Zoom, 3);

        window.SelectTool(Tool.Hand);
        var origin = At(0, 0);
        Drag(new SKPoint(300, 200), new SKPoint(350, 240));
        Assert.NotEqual(origin, At(0, 0));

        window.SelectTool(Tool.Brush);
        window.MouseDown(At(100, 100), MouseButton.Left);
        window.MouseMove(At(200, 100), RawInputModifiers.LeftMouseButton);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(200, 100), MouseButton.Left);
        Assert.False(session.CanUndo);
        Assert.False(session.IsInteracting);
    }

    [AvaloniaFact]
    public void Alt_drag_moves_a_copy_and_escape_or_a_click_leaves_no_copy()
    {
        var box = Rendering.Pixels.NewColor(100, 100); box.Erase(SKColors.Red);
        var original = session.AddImageLayer("box", box, new SKPoint(300, 200));
        var start = original.Transform;
        session.View = session.View with { Snap = false };
        window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
        Drag(new SKPoint(300, 200), new SKPoint(350, 230), RawInputModifiers.Alt);
        var copy = session.ActiveLayer!;
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(start, original.Transform);
        Assert.Equal((start.X + 50, start.Y + 30), (copy.Transform.X, copy.Transform.Y));
        Assert.Equal("Duplicate and Move", session.History.UndoName);
        session.Undo(); Assert.Equal(2, session.Document.Layers.Count);
        session.Redo(); Assert.Equal(3, session.Document.Layers.Count);
        window.MouseDown(At(350, 230), MouseButton.Left, RawInputModifiers.Alt);
        window.MouseMove(At(390, 250), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(390, 250), MouseButton.Left);
        Assert.Equal(3, session.Document.Layers.Count);
        Click(345, 225, RawInputModifiers.Alt);
        Assert.Equal(3, session.Document.Layers.Count);
    }

    [AvaloniaFact]
    public void Move_picks_pixels_below_the_selected_frame_and_clears_selection_outside_canvas()
    {
        var background = session.ActiveLayer!;
        var ring = session.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Red, 0) { FillEnabled = false, Stroke = (uint)SKColors.Red, StrokeWidth = 8 }, new SKRect(150, 100, 450, 300))!;
        window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
        Click(300, 200); // The frame contains this click, but the ring's center has no pixels.
        Assert.Equal(background.Id, session.ActiveLayer!.Id);
        Click(153, 170); Assert.Equal(ring.Id, session.ActiveLayer!.Id);
        Click(640, 200);
        Assert.Null(session.ActiveLayer); Assert.Empty(session.Document.SelectedLayerIds);
        Assert.False(session.IsInteracting);
        window.MouseDown(At(630, 230), MouseButton.Left);
        window.MouseMove(At(650, 250), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(650, 250), MouseButton.Left);
        Assert.Null(session.ActiveLayer);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Move_drags_an_off_canvas_layer_back_into_view(bool showTransformControls)
    {
        var pixels = Rendering.Pixels.NewColor(50, 50);
        pixels.Erase(SKColors.Red);
        if (showTransformControls)
        {
            // The center of the selected frame is draggable even where the layer is transparent.
            using var canvas = new SKCanvas(pixels);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Clear };
            canvas.DrawRect(15, 0, 20, 50, paint);
        }
        var layer = session.AddImageLayer("Off canvas", pixels, new SKPoint(650, 200), fit: false);
        session.View = session.View with { Snap = false };
        window.SelectTool(Tool.Move);
        window.Canvas.ShowTransformControls = showTransformControls;
        window.Canvas.ZoomTo(.75);
        Dispatcher.UIThread.RunJobs();

        Drag(new SKPoint(650, 200), new SKPoint(550, 200));

        Assert.Equal(layer.Id, session.ActiveLayer!.Id);
        Assert.Equal(525, layer.Transform.X);
        Assert.Equal(SKColors.Red, session.Composite().GetPixel(530, 200));
        Assert.Equal("Move", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Move_ignores_masked_pixels_and_clicking_transparent_canvas_selects_nothing()
    {
        var transparent = EditorSession.NewCanvas(600, 400, null);
        window.AddSession(transparent);
        var lower = transparent.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new SKRect(100, 100, 400, 300))!;
        var upper = transparent.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)SKColors.Red, 0), new SKRect(150, 120, 350, 280))!;
        var mask = Rendering.Pixels.NewMask(200, 160); mask.Erase(SKColors.Transparent);
        upper.Mask = mask;
        window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
        Click(250, 200); Assert.Equal(lower.Id, transparent.ActiveLayer!.Id);
        Click(500, 350); Assert.Null(transparent.ActiveLayer);
    }

    [AvaloniaFact]
    public void Move_checks_nested_group_masks_and_visibility_from_the_top_of_the_stack()
    {
        var document = new Document(600, 400);
        var pixels = Rendering.Pixels.NewColor(600, 400);
        pixels.Erase(SKColors.White);
        var lower = Layer.Raster("Lower", pixels);
        var child = Layer.Raster("Child", pixels);
        var mask = Rendering.Pixels.NewMask(600, 400);
        using (var canvas = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(0, 0, 300, 400, paint);
        var outer = new Layer { Kind = LayerKind.Group, Mask = mask };
        var inner = new Layer { Kind = LayerKind.Group, Opacity = .5 };
        inner.Children.Add(child);
        outer.Children.Add(inner);
        document.Layers.AddRange([lower, outer]);
        var nested = new EditorSession(document);
        window.AddSession(nested);
        window.Canvas.ShowTransformControls = false;
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();

        Click(200, 180);
        Assert.Equal(outer.Id, nested.ActiveLayer!.Id);
        Click(200, 180, RawInputModifiers.Control);
        Assert.Equal(child.Id, nested.ActiveLayer!.Id);
        Click(410, 180);
        Assert.Equal(lower.Id, nested.ActiveLayer!.Id);
        nested.SetVisible(nested.Document.Find(outer.Id)!, false);
        Click(200, 180);
        Assert.Equal(lower.Id, nested.ActiveLayer!.Id);
    }

    private static void TestColor(SKColor expected, SKColor actual) =>
        Assert.True(Math.Abs(expected.Red - actual.Red) < 3 && Math.Abs(expected.Green - actual.Green) < 3 && Math.Abs(expected.Blue - actual.Blue) < 3, $"expected {expected}, found {actual}");
}

public class ZoomAndCropTests
{
    [AvaloniaFact]
    public void Zoom_in_and_out_step_through_fixed_stops_and_come_back_without_drift()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.AddSession(EditorSession.NewCanvas(3000, 2000, SKColors.White));
        Dispatcher.UIThread.RunJobs();
        var canvas = window.Canvas;
        canvas.ZoomTo(1);
        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(1.25, canvas.Zoom);
        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control | RawInputModifiers.Shift); // '+' is Shift and '='.
        Assert.Equal(1.5, canvas.Zoom);
        window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        Assert.Equal(1.25, canvas.Zoom);
        canvas.ZoomTo(0.5);
        for (var i = 0; i < 10; i++) canvas.ZoomIn();
        for (var i = 0; i < 10; i++) canvas.ZoomOut();
        Assert.Equal(0.5, canvas.Zoom);
        canvas.ZoomTo(0.55);                                              // Off a stop: the next stop, not a multiple.
        canvas.ZoomIn();
        Assert.Equal(2 / 3.0, canvas.Zoom, 9);
        canvas.ZoomTo(CanvasView.ZoomStops[^1]);
        canvas.ZoomIn();
        Assert.Equal(CanvasView.ZoomStops[^1], canvas.Zoom);              // Clamped at the last stop.
    }

    [AvaloniaFact]
    public void The_crop_box_starts_at_the_selection_and_follows_the_ratio()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(400, 300, SKColors.White);
        window.AddSession(session);
        session.SelectRect(new SKRect(40, 50, 140, 130));
        window.SelectTool(Tool.Crop);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRect(40, 50, 140, 130), window.Canvas.CropRect);
        session.CropRatio = "1:1";
        window.Canvas.ChangeCropRatio();
        var square = window.Canvas.CropRect!.Value;
        Assert.Equal(square.Width, square.Height, 3);
        Assert.Equal(90, square.MidX, 0.5);                                // Reshaped about its center.
        Assert.Equal(90, square.MidY, 0.5);
        Assert.Contains("3:4", EditorSession.CropRatios);
        Assert.Contains("9:16", EditorSession.CropRatios);
        session.CropRatio = "9:16";
        Assert.Equal(9 / 16.0, session.CropAspect);
        window.SelectTool(Tool.Move);
        Assert.Null(window.Canvas.CropRect);
    }
}

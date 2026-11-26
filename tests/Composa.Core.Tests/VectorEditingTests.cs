using Composa.Editing;
using Composa.IO;
using Composa.Model;
using SkiaSharp;

namespace Composa.Core.Tests;

public class VectorEditingTests
{
    private static readonly BezierNode A = new(.05, .7, .05, .7, .11, -.22);
    private static readonly BezierNode B = new(.94, .19, .84, 1.18, .94, .19);

    [Fact]
    public void Cutting_a_cubic_retains_both_halves_and_keeps_other_contours_closed()
    {
        var curve = new VectorPath { Nodes = [A, B] };
        var cut = curve.CutSegment(0, .5);
        Assert.Equal(4, cut.Nodes.Length);
        Assert.True(cut.Nodes[2].StartsContour);
        Assert.Equal(cut.Nodes[1].X, cut.Nodes[2].X);
        Assert.Equal(cut.Nodes[1].Y, cut.Nodes[2].Y);
        Assert.Equal(-1, cut.SegmentEnd(1));
        Assert.Equal(3, cut.SegmentEnd(2));
        using var full = curve.Build(240, 120);
        using var split = cut.Build(240, 120);
        Assert.Equal(full.TightBounds.Top, split.TightBounds.Top, 3);
        Assert.Equal(full.TightBounds.Bottom, split.TightBounds.Bottom, 3);

        var compound = new VectorPath { Closed = true, Nodes = [BezierNode.Corner(0, 0), BezierNode.Corner(.3, 0), BezierNode.Corner(.15, .5),
            BezierNode.Corner(.6, 0) with { StartsContour = true, StartsUnion = true }, BezierNode.Corner(.9, 0), BezierNode.Corner(.75, .5)] };
        var opened = compound.CutSegment(0, .5);
        Assert.True(opened.Closed);
        Assert.False(opened.IsFullyClosed);
        Assert.Equal(-1, opened.SegmentEnd(4));
        Assert.Equal(5, opened.SegmentEnd(7));
        using var fill = opened.Build(240, 120, closedOnly: true);
        Assert.False(fill.Contains(36, 12));
        Assert.True(fill.Contains(180, 12));
    }

    [Fact]
    public void Stroke_expands_for_hollow_marker_and_survives_undo_redo_and_project_roundtrip()
    {
        var session = EditorSession.NewCanvas(300, 180);
        var line = session.AddLine(new SKPoint(50, 80), new SKPoint(220, 80), SKColors.Black, 8)!;
        var before = line.Pixels;
        var options = new VectorStroke { Cap = StrokeCap.Round, End = StrokeMarker.HollowCircle, Start = StrokeMarker.SlimArrow };
        session.ChangeShapeStyle(line, line.Shape! with { StrokeOptions = options, Dash = new StrokeDash([14, 10], 3) });
        Assert.True(line.Pixels!.Width > before!.Width);
        var visible = session.Composite();
        Assert.Equal(0, visible.GetPixel(235, 80).Alpha);
        Assert.True(visible.GetPixel(235, 69).Alpha > 200);
        Assert.True(visible.GetPixel(57, 80).Alpha > 200);
        Assert.Equal(0, visible.GetPixel(66, 80).Alpha);
        session.Undo();
        Assert.Same(before, session.ActiveLayer!.Pixels);
        session.Redo();
        Assert.Equal(options, session.ActiveLayer!.Shape!.StrokeOptions);
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream); stream.Position = 0;
        Assert.Equal(session.ActiveLayer.Shape, ProjectFile.Read(stream).Find(line.Id)!.Shape);
    }

    [Fact]
    public void Stroke_width_profiles_change_the_ink_and_closed_strokes_align_to_the_outline()
    {
        var line = new ShapeStyle(ShapeKind.Line, (uint)SKColors.Black, 0) { LineWidth = 12,
            StartX = .1, StartY = .5, EndX = .9, EndY = .5, StrokeOptions = new() { Profile = StrokeProfile.TaperStart, Cap = StrokeCap.Butt } };
        using var tapered = EditorSession.RenderShape(line, 220, 100);
        Assert.Equal(0, tapered.GetPixel(40, 46).Alpha);
        Assert.True(tapered.GetPixel(185, 46).Alpha > 200);

        var outline = new VectorPath { Closed = true, Nodes = [BezierNode.Corner(.2, .2), BezierNode.Corner(.8, .2),
            BezierNode.Corner(.8, .8), BezierNode.Corner(.2, .8)] };
        var square = new ShapeStyle(ShapeKind.Path, 0, 0) { Path = outline, FillEnabled = false, Stroke = (uint)SKColors.Black,
            StrokeWidth = 10, StrokeOptions = new() { Position = StrokePosition.Inside, Join = StrokeJoin.Round } };
        using var inner = EditorSession.RenderShape(square, 100, 100);
        using var outer = EditorSession.RenderShape(square with { StrokeOptions = square.StrokeOptions with { Position = StrokePosition.Outside } }, 100, 100);
        Assert.True(inner.GetPixel(50, 25).Alpha > 200);
        Assert.Equal(0, inner.GetPixel(50, 15).Alpha);
        Assert.True(outer.GetPixel(50, 15).Alpha > 200);
        Assert.Equal(0, outer.GetPixel(50, 25).Alpha);
    }

    [Theory]
    [InlineData(.03, false)]
    [InlineData(.37, false)]
    [InlineData(.87, true)]
    public void Removing_a_subdivision_recovers_asymmetric_cubic(double t, bool closing)
    {
        var path = new VectorPath { Nodes = closing ? [B, BezierNode.Corner(.4, .5), A] : [A, B], Closed = closing };
        int segment = closing ? 2 : 0;
        var restored = path.SplitSegment(segment, t).RemoveNode(segment + 1, 3400, 230);
        Assert.Equal(path.Closed, restored.Closed);
        var left = restored.Nodes[segment]; var right = restored.Nodes[(segment + 1) % restored.Nodes.Length];
        Assert.InRange(Math.Abs(left.OutX - A.OutX) * 3400 + Math.Abs(left.OutY - A.OutY) * 230, 0, .001);
        Assert.InRange(Math.Abs(right.InX - B.InX) * 3400 + Math.Abs(right.InY - B.InY) * 230, 0, .001);
    }

    [Fact]
    public void Cutting_open_and_closing_segments_leaves_no_bridge()
    {
        var path = new VectorPath { Nodes = [BezierNode.Corner(0, 0), BezierNode.Corner(.25, .25), BezierNode.Corner(.75, .75), BezierNode.Corner(1, 1)] };
        var cut = path.RemoveSegment(1);
        Assert.True(cut.HasSegment(0)); Assert.False(cut.HasSegment(1)); Assert.True(cut.HasSegment(2));
        Assert.Equal(-1, cut.NearestSegment(SKMatrix.CreateScale(200, 300), new SKPoint(100, 150), 3).Segment);
        var opened = (path with { Closed = true }).RemoveSegment(1);
        Assert.False(opened.Closed); Assert.Equal(path.Nodes[2], opened.Nodes[0]); Assert.Equal(path.Nodes[1], opened.Nodes[^1]);
    }

    [Fact]
    public void Fitting_an_edited_join_keeps_the_silhouette_closer_than_dropping_the_anchor()
    {
        var split = new VectorPath { Nodes = [A, B] }.SplitSegment(0, .4);
        var mid = split.Nodes[1];
        split.Nodes[1] = mid with { X = mid.X + .014, InX = mid.InX + .014, OutX = mid.OutX + .014 };
        var fit = split.RemoveNode(1, 1600, 750);
        var naive = split with { Nodes = [split.Nodes[0], split.Nodes[2]] };
        var scale = SKMatrix.CreateScale(1600, 750);
        double Error(VectorPath candidate)
        {
            double total = 0;
            for (int edge = 0; edge < 2; edge++) for (int i = 1; i < 20; i++)
            {
                var p = split.SplitSegment(edge, i / 20.0).Nodes[edge + 1];
                var target = scale.MapPoint((float)p.X, (float)p.Y);
                var hit = candidate.NearestSegment(scale, target, 180);
                Assert.Equal(0, hit.Segment);
                var q = candidate.SplitSegment(0, hit.T).Nodes[1];
                total += SKPoint.Distance(target, scale.MapPoint((float)q.X, (float)q.Y));
            }
            return total;
        }
        Assert.True(Error(fit) < .3 * Error(naive));
    }

    [Fact]
    public void Long_rotated_segment_can_be_split_precisely_between_percent_samples()
    {
        var path = new VectorPath { Nodes = [A, B] }; var point = path.SplitSegment(0, .34629).Nodes[1];
        var matrix = SKMatrix.CreateScale(90000, 20000).PostConcat(SKMatrix.CreateRotationDegrees(27));
        var target = matrix.MapPoint((float)point.X, (float)point.Y);
        var hit = path.NearestSegment(matrix, target, 5);
        Assert.Equal(0, hit.Segment); Assert.InRange(Math.Abs(hit.T - .34629), 0, .000002);
    }

    [Fact]
    public void Paint_alpha_multiplies_stop_opacity_and_each_paint_survives_project_roundtrip()
    {
        var ramp = GradientRamp.Between(0x00ff0000, 0xff0000ff);
        var style = new ShapeStyle(ShapeKind.Rectangle, 0x80000000, 0)
        {
            FillGradient = new(ramp),
            Stroke = 0xff000000,
            StrokeWidth = 8,
            StrokeGradient = new(GradientRamp.Between(0xff00ff00, 0xffffff00), true)
        };
        var session = EditorSession.NewCanvas(180, 120);
        var layer = session.AddShape(style, new SKRect(10, 10, 170, 110))!;
        var pixels = layer.Pixels!;
        Assert.InRange((int)pixels.GetPixel(80, 50).Alpha, 62, 67);
        Assert.True(pixels.GetPixel(144, 50).Blue > pixels.GetPixel(16, 50).Blue);
        Assert.True(pixels.GetPixel(80, 2).Green > 240);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        Assert.Equal(style, ProjectFile.Read(stream).Find(layer.Id)!.Shape);
    }

    [Fact]
    public void Hidden_stroke_keeps_its_color_ramp_and_width_on_reopen()
    {
        var session = EditorSession.NewCanvas(120, 80);
        var paint = new ShapeStyle(ShapeKind.Rectangle, 0xff000000, 0)
        {
            FillEnabled = false,
            Stroke = 0xffff0000,
            StrokeWidth = 8,
            StrokeEnabled = false,
            StrokeGradient = new(GradientRamp.Presets["Sunset"])
        };
        var layer = session.AddShape(paint, new SKRect(10, 10, 110, 70))!;
        Assert.Equal(0, layer.Pixels!.GetPixel(50, 2).Alpha);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var reopened = new EditorSession(ProjectFile.Read(stream)); var restored = reopened.Document.Find(layer.Id)!;
        Assert.Equal(paint, restored.Shape);
        reopened.ChangeShapeStyle(restored, restored.Shape! with { StrokeEnabled = true });
        Assert.Equal(255, restored.Pixels!.GetPixel(50, 2).Alpha);
    }

    [Theory]
    [InlineData(ShapeKind.Ellipse, 4)]
    [InlineData(ShapeKind.Rectangle, 4)]
    [InlineData(ShapeKind.RoundedRectangle, 8)]
    [InlineData(ShapeKind.Line, 2)]
    public void Primitives_convert_to_editable_anchors_and_undo(ShapeKind kind, int count)
    {
        var s = EditorSession.NewCanvas(200, 120);
        var l = kind == ShapeKind.Line ? s.AddLine(new(10, 10), new(170, 110), new SKColor(0xff972915), 4)! : s.AddShape(new ShapeStyle(kind, 0xff972915, 16) { LineWidth = 4, Stroke = 0xff184956, StrokeWidth = 3 }, new SKRect(10, 10, 170, 110))!;
        var before = l.Pixels!; s.ConvertShapeToPath(l);
        Assert.Equal(count, l.Shape!.Path!.Nodes.Length);
        double difference = 0;
        for (int y = 0; y < before.Height; y++) for (int x = 0; x < before.Width; x++)
        {
            var a = before.GetPixel(x, y); var b = l.Pixels!.GetPixel(x, y);
            difference += Math.Abs(a.Alpha - b.Alpha);
        }
        Assert.InRange(difference / (before.Width * before.Height), 0, 1);
        s.Undo(); Assert.Equal(kind, s.ActiveLayer!.Shape!.Kind); Assert.Same(before, s.ActiveLayer.Pixels);
    }
}

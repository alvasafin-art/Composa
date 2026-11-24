using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class BrushTipTests
{
    [Fact]
    public void Factory_presets_use_the_same_defaults_except_hardness()
    {
        Assert.Collection(BrushPresets.All,
            soft => Assert.Equal(new BrushPreset("Soft", new BrushSettings { Hardness = 0, Flow = 1 }), soft),
            hard => Assert.Equal(new BrushPreset("Hard", new BrushSettings { Hardness = 1, Flow = 1 }), hard));
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(.25, 0, 1)]
    [InlineData(.25, 90, 1)]
    [InlineData(.30, 45, 0)]
    public void Tip_sample_matches_an_applied_dab(double roundness, double angle, double hardness)
    {
        const int side = 72;
        var brush = new BrushSettings { Size = 40, Roundness = roundness, Angle = angle, Hardness = hardness, Flow = .8, Opacity = .5 };
        using var preview = BrushStroke.RenderTip(brush, side);
        using var source = Pixels.NewColor(side, side);
        using var stroke = new BrushStroke(source, brush, BrushMode.Paint, SKColors.White);
        using var painted = stroke.Working;
        stroke.AddPoint(new(side / 2f, side / 2f));
        Assert.Equal(painted.Bytes, preview.Bytes);
        Assert.InRange(preview.GetPixel(36, 36).Alpha, 99, 102);
        if (roundness < 1 && angle == 0)
        {
            Assert.Equal(102, preview.GetPixel(51, 36).Alpha);
            Assert.Equal(0, preview.GetPixel(36, 51).Alpha);
        }
        if (roundness < 1 && angle == 90)
        {
            Assert.Equal(0, preview.GetPixel(51, 36).Alpha);
            Assert.Equal(102, preview.GetPixel(36, 51).Alpha);
        }
        if (angle == 45)
        {
            Assert.True(preview.GetPixel(45, 26).Alpha > 0);
            Assert.Equal(0, preview.GetPixel(45, 45).Alpha);
        }
    }

    [Fact]
    public void Narrow_soft_dab_is_visible_between_pixel_centers()
    {
        using var source = Pixels.NewColor(24, 24);
        using var stroke = new BrushStroke(source, new() { Size = 2, Roundness = .1, Hardness = 0 },
            BrushMode.Paint, SKColors.White);
        using var painted = stroke.Working;
        stroke.AddPoint(new(12, 12));
        Assert.InRange(painted.GetPixel(12, 12).Alpha, 1, 40);
    }

    [Theory]
    [InlineData(Tool.Brush, ColorDepth.Eight, false)]
    [InlineData(Tool.Eraser, ColorDepth.Eight, false)]
    [InlineData(Tool.Brush, ColorDepth.Sixteen, false)]
    [InlineData(Tool.Eraser, ColorDepth.Sixteen, false)]
    [InlineData(Tool.Brush, ColorDepth.Sixteen, true)]
    [InlineData(Tool.Eraser, ColorDepth.Sixteen, true)]
    public void First_dab_fast_turns_and_undo_work_on_each_target(Tool tool, ColorDepth depth, bool mask)
    {
        var background = tool == Tool.Brush && !mask ? SKColors.Transparent : SKColors.White;
        var sequential = EditorSession.NewCanvas(420, 370, background, depth);
        var batched = EditorSession.NewCanvas(420, 370, background, depth);
        var brush = new BrushSettings
        {
            Size = 150, Roundness = .24, Angle = 33, Hardness = .75, Flow = .7, Opacity = .6, PressureFlow = true
        };
        var start = new SKPoint(124.5f, 120.5f);
        foreach (var session in new[] { sequential, batched })
        {
            if (mask) session.AddMask(session.ActiveLayer!);
            session.Tool = tool;
            session.Brush = brush;
            session.Foreground = mask ? SKColors.Black : SKColors.White;
        }

        SKBitmap Target(EditorSession session) => mask ? session.ActiveLayer!.Mask! : session.ActiveLayer!.Pixels!;
        var original = Target(batched);
        var before = original.Bytes;
        float Alpha(EditorSession session, int x, int y) => mask
            ? Target(session).GetPixel(x, y).Alpha / 255f
            : new PixelBuffer(Target(session)).Read(x, y).A;
        var baseAlpha = Alpha(batched, 124, 120);
        var history = batched.History.Count;

        foreach (var session in new[] { sequential, batched })
        {
            Assert.True(session.BeginStroke(start, out var reason, pressure: .65f), reason);
            Assert.InRange(Math.Abs(Alpha(session, 124, 120) - baseAlpha), .26f, .29f);
        }

        (SKPoint Point, float Pressure)[] points =
        [
            (new(198.5f, 120.5f), .89f), (new(198.5f, 186.5f), .2f),
            (new(108.5f, 186.5f), .66f), (new(226.5f, 288.5f), 1)
        ];
        foreach (var (point, pressure) in points) sequential.ContinueStroke(point, pressure);
        batched.ContinueStrokeSamples(points);
        Assert.Equal(Target(sequential).Bytes, Target(batched).Bytes);
        foreach (var (x, y) in new[] { (160, 120), (198, 154), (150, 186) })
            Assert.InRange(Math.Abs(Alpha(batched, x, y) - baseAlpha), .03f, .601f);

        sequential.EndStroke();
        batched.EndStroke();
        Assert.Equal(history + 1, batched.History.Count);
        Assert.Equal(before, original.Bytes);
        var after = Target(batched).Bytes;
        Assert.Equal(Target(sequential).Bytes, after);
        batched.Undo();
        Assert.Equal(before, Target(batched).Bytes);
        batched.Redo();
        Assert.Equal(after, Target(batched).Bytes);
    }
}

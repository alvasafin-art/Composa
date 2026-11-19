using Composa.Editing;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SpotHealingQualityTests
{
    [Theory]
    [InlineData(Model.ColorDepth.Eight)]
    [InlineData(Model.ColorDepth.Sixteen)]
    public void Fractional_clone_samples_an_antialiased_edge_at_the_requested_offset(Model.ColorDepth depth)
    {
        using var original = Pixels.NewColor(40, 24, depth);
        original.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(original)) using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(new SKRect(4, 0, 8, 24), paint);
        using var stroke = new BrushStroke(original, new BrushSettings { Size = 6, Hardness = 1 }, BrushMode.Clone, SKColors.Black)
            { CloneSource = original, CloneOffset = new(-17, 0), CloneFraction = new(.5f, 0) };
        stroke.AddPoint(new SKPoint(20.5f, 10.5f));
        using var painted = stroke.Working;
        Assert.InRange(new PixelBuffer(painted).Read(20, 10).R, .499f, .503f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repair_touching_canvas_edge_uses_only_intact_boundary_colors(bool highPrecision)
    {
        using var source = Pixels.NewWorkingColor(64, 48,
            highPrecision ? Model.ColorDepth.Sixteen : Model.ColorDepth.Eight, ColorManagement.RgbProfile.Srgb);
        source.Erase(new SKColor(140, 160, 180));
        using var mask = Pixels.NewMask(64, 48);
        var area = new SKRect(0, 14, 9, 28);
        using (var canvas = new SKCanvas(source)) using (var paint = new SKPaint { Color = SKColors.Black })
            canvas.DrawRect(area, paint);
        using (var canvas = new SKCanvas(mask)) using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(area, paint);
        using var result = Inpaint.Fill(source, mask, coherentSpot: true);
        Assert.Equal(source.GetPixel(20, 15), result.GetPixel(0, 20));
        Assert.Equal(SKColors.Black, source.GetPixel(0, 20));
    }

    [Fact]
    public void Long_spot_stroke_over_a_solid_label_restores_the_exact_background()
    {
        var session = EditorSession.NewCanvas(240, 80, new SKColor(127, 100, 83));
        using var label = Pixels.NewColor(240, 80);
        label.Erase(new SKColor(127, 100, 83));
        using (var canvas = new SKCanvas(label))
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            for (int x = 30; x < 160; x += 17) canvas.DrawRect(new SKRect(x, 30, x + 4, 49), paint);
        session.AddImageLayer("Label", Pixels.Clone(label), fit: false);
        session.Tool = Tool.SpotHealing;
        session.SampleAllLayers = false;
        session.Brush = new BrushSettings { Size = 32, Hardness = 1 };
        Assert.True(session.BeginStroke(new SKPoint(27, 40), out _));
        session.ContinueStroke(new SKPoint(169, 40));
        session.EndStroke();
        var result = session.ActiveLayer!.Pixels!;
        for (int y = 30; y < 49; y++)
        for (int x = 30; x < 160; x++)
            Assert.Equal(new SKColor(127, 100, 83), result.GetPixel(x, y));
    }

    [Fact]
    public void Unaligned_clone_tracks_a_visible_donor_on_a_rotated_empty_layer()
    {
        var session = EditorSession.NewCanvas(96, 80, SKColors.Navy);
        session.SelectRect(new SKRect(10, 10, 26, 26)); session.Fill(SKColors.Orange); session.Deselect();
        var layer = session.AddBlankLayer();
        layer.Transform = layer.Transform with { Rotation = 30 };
        session.Tool = Tool.CloneStamp; session.SampleAllLayers = true; session.CloneAligned = false;
        session.Brush = new BrushSettings { Size = 8, Hardness = 1 };
        session.SetCloneSource(new SKPoint(18, 18));
        Assert.True(session.BeginStroke(new SKPoint(48, 40), out _));
        session.ContinueStroke(new SKPoint(52, 40));
        Assert.Equal(new SKPoint(22, 18), session.CloneSamplePoint(new SKPoint(52, 40)));
        session.EndStroke();
        using var result = session.Flatten();
        Assert.Equal(SKColors.Orange, result.GetPixel(48, 40));
        Assert.Equal(new SKPoint(18, 18), session.CloneSamplePoint(new SKPoint(52, 40)));
    }

    [Fact]
    public void A_grainy_gradient_keeps_natural_texture_without_noise_spikes()
    {
        using var clean = Pixels.NewColor(128, 96); var random = new Random(179);
        for (var y = 0; y < clean.Height; y++) for (var x = 0; x < clean.Width; x++)
        {
            var value = 110 + x / 3 + y / 5 + random.Next(-7, 8);
            clean.SetPixel(x, y, new SKColor((byte)value, (byte)(value + 16), (byte)(value + 25)));
        }
        using var dirty = Pixels.Clone(clean); using var mask = Pixels.NewMask(128, 96);
        using (var canvas = new SKCanvas(mask)) using (var paint = new SKPaint { Color = SKColors.White }) canvas.DrawCircle(64, 48, 10, paint);
        for (var y = 44; y < 53; y++) for (var x = 60; x < 69; x++) dirty.SetPixel(x, y, SKColors.Black);
        using var result = Inpaint.Fill(dirty, mask, coherentSpot: true);
        double bias = 0, roughness = 0, baseline = 0; int count = 0;
        for (var y = 0; y < 96; y++) for (var x = 0; x < 128; x++)
        {
            if (mask.GetPixel(x, y).Alpha == 0) Assert.Equal(dirty.GetPixel(x, y), result.GetPixel(x, y));
            if (x is < 59 or > 68 || y is < 43 or > 52) continue;
            bias += result.GetPixel(x, y).Red - clean.GetPixel(x, y).Red;
            roughness += Math.Abs(result.GetPixel(x, y).Red - result.GetPixel(x + 1, y).Red);
            baseline += Math.Abs(clean.GetPixel(x, y).Red - clean.GetPixel(x + 1, y).Red); count++;
        }
        Assert.True(Math.Abs(bias / count) < 4, $"Tone drift: {bias / count:0.00}");
        Assert.InRange(roughness / Math.Max(1, baseline), .55, 1.35);
        Assert.Equal(SKColors.Black, dirty.GetPixel(64, 48));
    }

    [Fact]
    public void Sample_all_layers_heals_onto_an_empty_layer_without_copying_the_whole_document()
    {
        var session = EditorSession.NewCanvas(100, 80, new SKColor(130, 150, 175));
        var source = session.ActiveLayer!;
        session.SelectRect(new SKRect(44, 34, 56, 46)); session.Fill(SKColors.Black); session.Deselect();
        var sourcePixels = source.Pixels!.GetPixelSpan().ToArray();
        var retouch = session.AddBlankLayer(); var original = retouch.Pixels!;
        session.Tool = Tool.SpotHealing; session.SampleAllLayers = true; session.Brush = new BrushSettings { Size = 26, Hardness = 1 };
        Assert.True(session.BeginStroke(new SKPoint(50, 40), out var problem)); Assert.Null(problem); session.EndStroke();
        Assert.Equal(new SKColor(130, 150, 175), retouch.Pixels!.GetPixel(50, 40));
        Assert.Equal((byte)0, retouch.Pixels.GetPixel(5, 5).Alpha);
        Assert.Equal(sourcePixels, source.Pixels.GetPixelSpan().ToArray());
        var repaired = retouch.Pixels.GetPixelSpan().ToArray();
        session.Undo(); Assert.Same(original, session.ActiveLayer!.Pixels); session.Redo();
        Assert.Equal(repaired, session.ActiveLayer!.Pixels!.GetPixelSpan().ToArray());
    }
}

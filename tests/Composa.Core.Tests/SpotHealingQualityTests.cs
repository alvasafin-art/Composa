using Composa.Editing;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SpotHealingQualityTests
{
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

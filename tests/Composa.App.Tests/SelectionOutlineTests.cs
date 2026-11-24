using Composa.App.Controls;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class SelectionOutlineTests
{
    [Theory]
    [InlineData(1f, 1f, -37.25f, -23.5f)]
    [InlineData(1f, 1.25f, 6.5f, -12.25f)]
    [InlineData(2f, 1.5f, -133.75f, -97.25f)]
    [InlineData(1.36f, 1.5f, -23.5f, -48.25f)]
    public void Batched_strokes_match_all_dash_phases_in_the_clipped_view(float zoom, float scaling, float x, float y)
    {
        const int width = 360, height = 264;
        using var mask = Pixels.NewMask(width, height);
        var rows = mask.GetPixelSpan();
        var stride = mask.RowBytes;
        var random = new Random(691);
        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
        {
            // One-pixel strands and islands alongside touching corners, holes and jagged edges.
            if (col < width / 2 ? col % 4 == 0 && row % 4 == 0 || col == 25 + row / 3
                : random.Next(10) < 5 || row % 8 < 2)
                rows[row * stride + col] = 255;
        }
        Pixels.Invalidate(mask);
        using var outline = SelectionMask.Outline(mask);
        var batches = CanvasView.SelectionOutlineBatches.Create(outline);
        Assert.NotNull(batches);
        using var expected = Pixels.NewColor(246, 181);
        using var actual = Pixels.NewColor(expected.Width, expected.Height);
        using var reference = new SKCanvas(expected);
        using var candidate = new SKCanvas(actual);
        var view = SKMatrix.CreateScale(zoom / scaling, zoom / scaling).PostConcat(SKMatrix.CreateTranslation(x, y));
        for (var phase = 0; phase < 8; phase++)
        {
            Render(reference, null, phase);
            Render(candidate, batches, phase);
            Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), $"Pixel mismatch in dash phase {phase}.");
        }

        void Render(SKCanvas canvas, CanvasView.SelectionOutlineBatches? split, int phase)
        {
            canvas.Clear(new SKColor(42, 130, 175));
            canvas.Save();
            canvas.Translate(.25f, .75f);
            canvas.Scale(scaling);
            canvas.ClipRect(SKRect.Create(8, 7, 208 / scaling, 145 / scaling));
            CanvasView.DrawAnts(canvas, outline, view, phase, scaling, split);
            canvas.Restore();
        }
    }
}

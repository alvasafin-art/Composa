using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public sealed class LayerEffectsPerformanceTests
{
    [Theory]
    [InlineData(false, false, 1f)]
    [InlineData(true, false, 1f)]
    [InlineData(false, true, 1f)]
    [InlineData(true, true, 1f)]
    [InlineData(false, false, .5f)]
    [InlineData(false, false, .71f)]
    [InlineData(true, false, .33f)]
    [InlineData(false, false, .19f)]
    [InlineData(true, true, .19f)]
    public void View_shows_the_current_mask_or_paint_with_exact_effect_pixels(bool editMask, bool cancel, float zoom)
    {
        var session = EditorSession.NewCanvas(2000, 1300, SKColors.White);
        var pixels = Pixels.NewLayerColor(1989, 1297, ColorDepth.Eight, Composa.ColorManagement.RgbProfile.Srgb);
        using (var canvas = new SKCanvas(pixels))
        using (var brush = new SKPaint { Color = new SKColor(33, 114, 175, 172), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(190, 170, 1850, 1100), 180, 180, brush);
        var mask = Pixels.NewLayerMask(pixels.Width, pixels.Height, 255);
        using (var canvas = new SKCanvas(mask))
        using (var brush = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true })
            canvas.DrawCircle(1100, 690, 160, brush);

        var layer = Layer.Raster("Paint", pixels, 8, 3);
        layer.Mask = mask;
        layer.Effects = new LayerEffects
        {
            Shadow = new() { Distance = 37, Blur = 67, Angle = 37 },
            OuterGlow = new() { Size = 24 },
            Stroke = new() { Size = 3 }
        };
        session.Document.Layers.Add(layer);
        session.SelectLayer(layer.Id);
        session.EditingMask = editMask;
        session.ViewZoom = zoom;
        session.Brush = new BrushSettings { Size = 76, Hardness = .6, Opacity = .73 };
        session.Foreground = editMask ? SKColors.Black : SKColors.OrangeRed;
        var view = new RenderView(zoom, zoom == 1 ? new SKPoint(970, 495) : new SKPoint(913.25f, 481.75f));
        var area = SKRectI.Create((int)(280 * zoom), (int)(250 * zoom));
        using var original = Pixels.NewColor(area.Width, area.Height);
        session.RenderView(original, area, view);

        Assert.True(session.BeginStroke(new SKPoint(1060, 525), out var problem, mode: BrushMode.Paint), problem);
        using var firstDab = Pixels.NewColor(area.Width, area.Height);
        session.RenderView(firstDab, area, view);
        session.ContinueStroke(new SKPoint(1120, 560));
        using var live = Pixels.NewColor(area.Width, area.Height);
        session.RenderView(live, area, view);
        Assert.False(original.GetPixelSpan().SequenceEqual(live.GetPixelSpan()));
        Assert.False(firstDab.GetPixelSpan().SequenceEqual(live.GetPixelSpan()));
        if (cancel) session.CancelStroke();
        else session.EndStroke();

        using var committed = Pixels.NewColor(area.Width, area.Height);
        session.RenderView(committed, area, view);
        using var final = Pixels.NewColor(area.Width, area.Height);
        DocumentRenderer.Render(session.Document, final, area, view);
        AssertExact(final, committed);
        Assert.True(final.GetPixelSpan().SequenceEqual((cancel ? original : live).GetPixelSpan()),
            $"Preview at {zoom:P0} differs from full rendering for {(editMask ? "mask" : "color")} painting.");
    }

    private static void AssertExact(SKBitmap expected, SKBitmap actual)
    {
        var left = expected.GetPixelSpan();
        var right = actual.GetPixelSpan();
        if (left.SequenceEqual(right)) return;
        var different = 0;
        var max = 0;
        var first = -1;
        for (var i = 0; i < left.Length; i++)
        {
            var delta = Math.Abs(left[i] - right[i]);
            if (delta == 0) continue;
            if (first < 0) first = i;
            different++;
            max = Math.Max(max, delta);
        }
        Assert.Fail($"{different} different channels, max {max}, first ({(first % expected.RowBytes) / 4}, {first / expected.RowBytes}).");
    }

    [Fact]
    public void Closing_a_project_evicts_its_effects_and_preserves_open_tab_cache_entries()
    {
        using var closedPixels = Pixels.NewColor(160, 120);
        using var sharedPixels = Pixels.NewColor(160, 120);
        closedPixels.Erase(SKColors.CornflowerBlue);
        var effects = new LayerEffects { Shadow = new() };
        var closing = new Document(160, 120);
        closing.Layers.Add(Layer.Raster("Private", closedPixels));
        closing.Layers.Add(Layer.Raster("Shared", sharedPixels));
        var other = new Document(160, 120);
        other.Layers.Add(Layer.Raster("Shared", sharedPixels));
        var retained = LayerEffectsRenderer.Cached(closedPixels, null, effects)!.Value;
        var shared = LayerEffectsRenderer.Cached(sharedPixels, null, effects)!.Value.Image;
        var closingSession = new EditorSession(closing);
        closingSession.Apply("Rename layer", () => closing.Layers[0].Name = "Renamed");
        var reader = closing.Clone();
        Assert.True(closingSession.CanUndo);
        closingSession.ReleaseClosedResources([new EditorSession(other)]);
        Assert.False(closingSession.CanUndo);
        Assert.Empty(closingSession.Document.Layers);
        Assert.Equal(2, closing.Layers.Count);
        Assert.Equal(SKColors.CornflowerBlue, reader.Layers[0].Pixels!.GetPixel(80, 60));
        Assert.Equal(SKColors.CornflowerBlue, retained.Image.GetPixel(retained.Inset + 80, retained.Inset + 60));
        Assert.NotSame(retained.Image, LayerEffectsRenderer.Cached(closedPixels, null, effects)!.Value.Image);
        Assert.Same(shared, LayerEffectsRenderer.Cached(sharedPixels, null, effects)!.Value.Image);
    }

    [Fact]
    public async Task Clearing_the_effect_cache_does_not_invalidate_an_image_already_returned_to_a_render()
    {
        using var source = Pixels.NewColor(1300, 900);
        source.Erase(SKColors.SteelBlue);
        var effects = new LayerEffects { Shadow = new() { Blur = 42 } };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => LayerEffectsRenderer.Cached(source, null, effects)!.Value)));
        Assert.All(results, result => Assert.Same(results[0].Image, result.Image));
        LayerEffectsRenderer.ClearCache();
        var sample = results[0].Image.GetPixel(results[0].Inset + 10, results[0].Inset + 10);
        Assert.Equal(SKColors.SteelBlue, sample);
        Assert.NotSame(results[0].Image, LayerEffectsRenderer.Cached(source, null, effects)!.Value.Image);
    }
}

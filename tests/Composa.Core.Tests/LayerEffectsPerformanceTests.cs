using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public sealed class LayerEffectsPerformanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Native_view_shows_the_current_mask_or_paint_with_exact_effect_pixels(bool editMask, bool cancel)
    {
        var session = EditorSession.NewCanvas(2000, 1300, SKColors.White);
        var pixels = Pixels.NewLayerColor(2000, 1300, ColorDepth.Eight, Composa.ColorManagement.RgbProfile.Srgb);
        using (var canvas = new SKCanvas(pixels))
        using (var brush = new SKPaint { Color = new SKColor(33, 114, 175, 172), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(190, 170, 1850, 1100), 180, 180, brush);
        var mask = Pixels.NewLayerMask(2000, 1300, 255);
        using (var canvas = new SKCanvas(mask))
        using (var brush = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true })
            canvas.DrawCircle(1100, 690, 160, brush);

        var layer = Layer.Raster("Paint", pixels);
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
        session.Brush = new BrushSettings { Size = 76, Hardness = .6, Opacity = .73 };
        session.Foreground = editMask ? SKColors.Black : SKColors.OrangeRed;
        var view = new RenderView(1, new SKPoint(970, 495));
        var area = SKRectI.Create(280, 250);
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
        Assert.True(final.GetPixelSpan().SequenceEqual(committed.GetPixelSpan()));
        Assert.True(final.GetPixelSpan().SequenceEqual((cancel ? original : live).GetPixelSpan()),
            $"Native preview differs from full rendering for {(editMask ? "mask" : "color")} painting.");
    }

    [Fact]
    public void Closing_a_project_evicts_its_effects_and_preserves_open_tab_cache_entries()
    {
        using var closedPixels = Pixels.NewColor(160, 120);
        using var sharedPixels = Pixels.NewColor(160, 120);
        var effects = new LayerEffects { Shadow = new() };
        var closing = new Document(160, 120);
        closing.Layers.Add(Layer.Raster("Private", closedPixels));
        closing.Layers.Add(Layer.Raster("Shared", sharedPixels));
        var other = new Document(160, 120);
        other.Layers.Add(Layer.Raster("Shared", sharedPixels));
        var retained = LayerEffectsRenderer.Cached(closedPixels, null, effects)!.Value.Image;
        var shared = LayerEffectsRenderer.Cached(sharedPixels, null, effects)!.Value.Image;
        new EditorSession(closing).ReleaseEffectCache([new EditorSession(other)]);
        Assert.Equal(SKColors.Transparent, retained.GetPixel(0, 0));
        Assert.NotSame(retained, LayerEffectsRenderer.Cached(closedPixels, null, effects)!.Value.Image);
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

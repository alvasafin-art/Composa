using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ViewportBandTests
{
    [Theory]
    [InlineData(ColorDepth.Eight, 1f)]
    [InlineData(ColorDepth.Sixteen, .75f)]
    public void An_effect_beyond_its_masked_clipping_base_remains_visible_in_the_previous_band(ColorDepth depth, float scale)
    {
        var profile = depth == ColorDepth.Sixteen ? RgbProfile.DisplayP3 : RgbProfile.Srgb;
        var document = new Document(840, 680) { Depth = depth, Profile = profile };
        var pixels = Pixels.NewWorkingColor(100, 90, depth, profile);
        pixels.Erase(SKColors.White);
        var baseLayer = Layer.Raster("Base", pixels);
        baseLayer.Transform = baseLayer.Transform with { X = 180, Y = 180 };
        baseLayer.Mask = Pixels.NewMask(100, 90);
        baseLayer.Mask.Erase(new SKColor(0, 0, 0, 180));
        baseLayer.Effects = new LayerEffects { Stroke = new StrokeEffect { Size = 20, Color = 0xffaa2233 } };
        var followerPixels = Pixels.NewWorkingColor(400, 300, depth, profile);
        followerPixels.Erase(SKColors.LightBlue);
        var follower = Layer.Raster("Clipped", followerPixels);
        follower.Clipped = true;
        follower.Blend = BlendMode.Multiply;
        document.Layers.AddRange([baseLayer, follower]);

        var view = new RenderView(scale, new SKPoint(11, 7));
        using var full = Pixels.NewWorkingColor(800, 640, ColorDepth.Eight, RgbProfile.Srgb);
        DocumentRenderer.Render(document, full, full.Info.Rect, view);
        int x = (int)((230 - view.Origin.X) * scale);
        int y = (int)((166 - view.Origin.Y) * scale);
        Assert.True(full.GetPixel(x, y).Alpha > 0);
        Assert.Equal(0, full.GetPixel(x, (int)((130 - view.Origin.Y) * scale)).Alpha);

        using var partial = Pixels.NewWorkingColor(full.Width, full.Height, ColorDepth.Eight, RgbProfile.Srgb);
        var area = new SKRectI(x - 10, y - 6, x + 10, y + 5);
        DocumentRenderer.Render(document, partial, area, view);
        for (var row = area.Top; row < area.Bottom; row++)
        for (var col = area.Left; col < area.Right; col++)
            Assert.Equal(full.GetPixel(col, row), partial.GetPixel(col, row));
    }
}

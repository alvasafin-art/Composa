using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class ViewportBandTests
{
    [Fact]
    public void A_spatial_adjustment_requires_a_full_visibility_refresh()
    {
        var session = EditorSession.NewCanvas(840, 640, SKColors.White);
        var background = session.Document.Layers[0];
        session.AddAdjustmentLayer(new GaussianBlurAdjustment { Radius = 8 });
        using var cached = Pixels.NewColor(700, 560);
        var view = new RenderView(.81f, SKPoint.Empty);
        var viewport = cached.Info.Rect;
        session.RenderView(cached, viewport, view);
        var before = cached.Bytes;
        session.SetVisible(background, false);

        Assert.False(session.TryRefreshVisibilityBands(cached, viewport, view, background.VisibleBounds));
        Assert.True(cached.GetPixelSpan().SequenceEqual(before));
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Toggling_a_clipped_follower_on_a_fractional_view_keeps_the_full_render_bytes(ColorDepth depth)
    {
        var profile = depth == ColorDepth.Sixteen ? RgbProfile.DisplayP3 : RgbProfile.Srgb;
        var document = new Document(1200, 1000) { Depth = depth, Profile = profile };
        var backdropPixels = Pixels.NewWorkingColor(1200, 1000, depth, profile);
        backdropPixels.Erase(new SKColor(51, 87, 109));
        document.Layers.Add(Layer.Raster("Backdrop", backdropPixels));
        var basePixels = Pixels.NewWorkingColor(400, 280, depth, profile);
        basePixels.Erase(new SKColor(211, 123, 94, 147));
        var clippingBase = Layer.Raster("Base", basePixels);
        clippingBase.Transform = clippingBase.Transform with { X = 195.3, Y = 270.7, Rotation = 17 };
        clippingBase.Mask = Pixels.NewMask(400, 280, 141);
        var followerPixels = Pixels.NewWorkingColor(97, 69, depth, profile);
        followerPixels.Erase(new SKColor(36, 62, 82));
        var follower = Layer.Raster("Clipped", followerPixels);
        follower.Transform = follower.Transform with { X = 288.5, Y = 412.2 };
        follower.Clipped = true;
        follower.Blend = BlendMode.Multiply;
        follower.Effects = new LayerEffects { Shadow = new ShadowEffect { Blur = 9, Distance = 7 } };
        var group = Layer.Group("Group");
        group.Children.AddRange([clippingBase, follower]);
        document.Layers.Add(group);

        var session = new EditorSession(document);
        var view = new RenderView(.81f, new SKPoint(13.35f, 15.75f));
        using var cached = Pixels.NewWorkingColor(792, 668, ColorDepth.Eight, RgbProfile.Srgb);
        using var full = Pixels.NewWorkingColor(cached.Width, cached.Height, ColorDepth.Eight, RgbProfile.Srgb);
        var viewport = cached.Info.Rect;
        session.RenderView(cached, viewport, view);
        var original = cached.Bytes;
        SKRect? damage = null;
        session.ViewportInvalidated += (_, visibility) => damage = visibility?.Bounds;
        foreach (var visible in new[] { false, true })
        {
            session.SetVisible(follower, visible);
            var bounds = Assert.IsType<SKRect>(damage);
            Assert.True(bounds.Contains(clippingBase.VisibleBounds));
            Assert.True(session.TryRefreshVisibilityBands(cached, viewport, view, bounds));
            session.RenderView(full, viewport, view);
            Assert.True(full.GetPixelSpan().SequenceEqual(cached.GetPixelSpan()));
            Assert.Equal(visible, cached.GetPixelSpan().SequenceEqual(original));
        }
    }

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

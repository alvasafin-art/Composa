using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Filters;
using SkiaSharp;

namespace Composa.Core.Tests;

public class WorkingColorEditingTests
{
    [Fact]
    public void Untagged_BGRA_sources_are_normalized_before_they_enter_committed_RGB_edits()
    {
        using var image = new SKBitmap(32, 24); image.Erase(new SKColor(100, 150, 200));
        var original = image.Bytes; var session = EditorSession.NewCanvas(32, 24);
        var layer = session.AddImageLayer("External bitmap", image, fit: false);
        Assert.Equal(SKColorType.Rgba8888, layer.Pixels!.ColorType); Assert.Equal(new SKColor(100, 150, 200), layer.Pixels.GetPixel(8, 8));
        session.SelectRect(new(0, 0, 4, 4)); session.Fill(SKColors.Red);
        Assert.Equal(SKColors.Red, layer.Pixels!.GetPixel(1, 1)); Assert.Equal(new SKColor(100, 150, 200), layer.Pixels.GetPixel(8, 8));
        Assert.Equal(original, image.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void White_balance_eyedropper_and_auto_use_the_same_working_TRC(bool p3)
    {
        using var image = Pixels.NewWorkingColor(16, 16, ColorDepth.Sixteen, p3 ? RgbProfile.DisplayP3 : RgbProfile.Rgb1998);
        var pixels = new PixelBuffer(image);
        for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++) pixels.Write(x, y, new(.45f, .5f, .48f, 1));
        var picked = CameraRawPixels.WhiteBalanceAt(image, 8, 8)!.Value; var auto = CameraRawPixels.AutoBalance(image)!.Value;
        Assert.InRange(Math.Abs(picked.Temperature - auto.Temperature), 0, .000001); Assert.InRange(Math.Abs(picked.Tint - auto.Tint), 0, .000001);
        using var corrected = CameraRawPixels.Apply(image, new() { Temperature = picked.Temperature, Tint = picked.Tint });
        var gray = new PixelBuffer(corrected).Read(8, 8);
        Assert.InRange(Math.Abs(gray.R - gray.G), 0, .00002); Assert.InRange(Math.Abs(gray.B - gray.G), 0, .00002);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Exposure_and_camera_raw_use_the_working_TRC_and_do_not_reuse_another_profiles_cache(ColorDepth depth)
    {
        var adjustment = new ExposureAdjustment { Exposure = 1 };
        foreach (var profile in new[] { RgbProfile.Rgb1998, RgbProfile.Srgb, RgbProfile.Rgb1998 })
        {
            using var image = Pixels.NewWorkingColor(512, 1, depth, profile); var data = new PixelBuffer(image);
            for (var x = 0; x < 512; x++) data.Write(x, 0, new((24000 + x) / 65535f, .2f, .3f, 1));
            using var camera = CameraRawPixels.Apply(image, new CameraRawSettings { Exposure = 1 });
            var input = data.Read(100, 0).R;
            var expected = profile.Equals(RgbProfile.Rgb1998) ? input * Math.Pow(2, 1 / 2.2) : CameraRawPixels.LinearToSrgb(CameraRawPixels.SrgbToLinear(input) * 2);
            adjustment.Apply(image);
            var tolerance = depth == ColorDepth.Sixteen ? .00002 : 1d / 255;
            Assert.InRange(Math.Abs(new PixelBuffer(image).Read(100, 0).R - expected), 0, tolerance);
            Assert.InRange(Math.Abs(new PixelBuffer(camera).Read(100, 0).R - expected), 0, tolerance);
            if (depth == ColorDepth.Sixteen)
            {
                Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(image).Read(x, 0).R).Distinct().Count());
                Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(camera).Read(x, 0).R).Distinct().Count());
            }
        }
    }

    private static readonly SKColor color = new(100, 150, 200);
    private static EditorSession Session(ColorDepth depth)
    {
        var document = new Document(128, 96) { Depth = depth, Profile = RgbProfile.Rgb1998 };
        return new EditorSession(document);
    }

    private static void AssertColor(SKBitmap image, int x, int y, double tolerance = 1d / 255)
    {
        Assert.Equal(RgbProfile.Rgb1998, Pixels.ProfileOf(image));
        var p = new PixelBuffer(image).Read(x, y).Straight;
        Assert.InRange(Math.Abs(p.R - color.Red / 255d), 0, tolerance);
        Assert.InRange(Math.Abs(p.G - color.Green / 255d), 0, tolerance);
        Assert.InRange(Math.Abs(p.B - color.Blue / 255d), 0, tolerance);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Fill_brush_shapes_text_gradients_and_effects_share_working_RGB_numbers(ColorDepth depth)
    {
        var session = Session(depth); var layer = session.AddBlankLayer();
        session.Fill(color); AssertColor(layer.Pixels!, 20, 20);
        using (var stroke = new BrushStroke(layer.Pixels!, new() { Size = 30, Hardness = 1, PressureSize = false }, BrushMode.Paint, color))
        {
            stroke.AddPoint(new(20, 20)); AssertColor(stroke.Working, 20, 20); stroke.Working.Dispose();
        }
        var shape = session.AddShape(new ShapeStyle(ShapeKind.Rectangle, (uint)color, 0), new(0, 0, 80, 60))!;
        Assert.Equal(depth, Pixels.DepthOf(shape.Pixels!)); AssertColor(shape.Pixels!, 20, 20);
        session.ChangeShapeStyle(shape, shape.Shape! with { CornerRadius = 10 }); AssertColor(shape.Pixels!, 20, 20);
        var text = session.AddText(new(10, 10), new TextStyle { Text = "MMMM", Size = 60, Color = (uint)color });
        session.Begin("Retype"); session.SetText(text, text.Text! with { Text = "MMMMM" }); session.Commit();
        var buffer = new PixelBuffer(text.Pixels!);
        var opaque = (from y in Enumerable.Range(0, text.Pixels!.Height) from x in Enumerable.Range(0, text.Pixels.Width)
                      where buffer.Read(x, y).A > .99 select (x, y)).First();
        Assert.Equal(depth, Pixels.DepthOf(text.Pixels)); AssertColor(text.Pixels, opaque.x, opaque.y);
        session.SelectLayer(layer.Id); session.Foreground = session.Background = color;
        var original = session.BeginGradient(layer); session.DrawGradient(layer, original, new(0, 0), new(128, 0)); session.Commit();
        AssertColor(layer.Pixels!, 20, 20);
        var effects = new LayerEffects { Stroke = new() { Color = (uint)color, Size = 4 },
            GradientOverlay = new() { StartColor = (uint)color, EndColor = (uint)color } };
        var rendered = LayerEffectsRenderer.Render(shape.Pixels!, null, effects)!.Value;
        using (rendered.Image)
        {
            AssertColor(rendered.Image, rendered.Inset + 20, rendered.Inset + 20);
            AssertColor(rendered.Image, rendered.Inset - 2, rendered.Inset + 20);
        }
    }

    [Fact]
    public void Foreign_sources_and_smart_object_updates_convert_to_parent_without_changing_original()
    {
        var session = Session(ColorDepth.Sixteen);
        using var source = Pixels.NewColor(128, 96, ColorDepth.Sixteen); Pixels.FillWorking(source, color);
        var bytes = source.Bytes; using var expected = Pixels.ConvertWorking(source, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        var layer = session.AddImageLayer("sRGB image", source, fit: false);
        Assert.Equal(expected.Bytes, layer.Pixels!.Bytes); Assert.Equal(bytes, source.Bytes);
        var smart = session.ConvertToSmartObject(); var original = smart.SmartObject!;
        var content = EditorSession.NewCanvas(128, 96, color, ColorDepth.Eight);
        session.UpdateSmartObject(original, content.Document);
        Assert.Equal(RgbProfile.Srgb, smart.SmartObject!.OpenDocument().Profile);
        Assert.Equal(RgbProfile.Rgb1998, Pixels.ProfileOf(smart.Pixels!));
        Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(smart.Pixels!));
        using var expected8 = Pixels.ConvertWorking(content.ActiveLayer!.Pixels!, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        Assert.Equal(expected8.Bytes, smart.Pixels!.Bytes);
        session.Undo(); Assert.Same(original, session.ActiveLayer!.SmartObject);
    }
}

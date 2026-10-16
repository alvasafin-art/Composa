using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using Composa.Filters;
using SkiaSharp;

namespace Composa.Core.Tests;

[Collection(ClipboardCollection.Name)]
public class HighPrecisionEditingTests
{
    [Fact]
    public void Selection_and_histogram_read_the_correct_float_channels()
    {
        using var source = Ramp();
        using var exact = MagicWand.Select(source, 240, 32, 0, contiguous: true, smooth: false);
        Assert.Equal(new SKRectI(240, 0, 241, 64), SelectionMask.Bounds(exact));
        var sampled = ColorRange.ColorAt(source, 240, 32);
        Assert.NotNull(sampled);
        using var range = ColorRange.Match(source, [sampled.Value], [], 1, false).Mask;
        Assert.True(range.GetPixel(240, 32).Alpha > 0);
        var histogram = Histogram.Of(source);
        Assert.Equal(512 * 64, histogram.Channel(0).Sum());
        Assert.InRange(histogram.Channel(0).Count(n => n > 0), 2, 4);
    }

    private static SKBitmap Ramp()
    {
        var bitmap = Pixels.NewColor(512, 64, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(bitmap);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var value = (24000 + x) / 65535f;
            pixels.Write(x, y, new PixelValue(value, value * .8f, value * .6f, 32767 / 65535f).Premultiplied);
        }
        return bitmap;
    }

    [Theory]
    [InlineData(BrushMode.Paint)]
    [InlineData(BrushMode.Erase)]
    [InlineData(BrushMode.Clone)]
    [InlineData(BrushMode.Healing)]
    [InlineData(BrushMode.Blur)]
    [InlineData(BrushMode.Smudge)]
    [InlineData(BrushMode.Dodge)]
    [InlineData(BrushMode.Burn)]
    [InlineData(BrushMode.Liquify)]
    public void Brush_modes_keep_float_pixels_and_locked_alpha_without_touching_the_source(BrushMode mode)
    {
        using var source = Ramp();
        var before = source.Bytes;
        using var stroke = new BrushStroke(source, new() { Size = 24, Hardness = .8, PressureSize = false, Opacity = .6 }, mode, SKColors.White)
        { CloneSource = source, CloneOffset = new(20, 0), LockTransparency = true };
        stroke.AddPoint(new(240, 32)); stroke.AddPoint(new(260, 32));
        Assert.Equal(SKColorType.RgbaF32, stroke.Working.ColorType);
        Assert.Equal(before, source.Bytes);
        var pixels = new PixelBuffer(stroke.Working); var original = new PixelBuffer(source);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 512; x++)
        {
            var p = pixels.Read(x, y);
            Assert.Equal(original.Read(x, y).A, p.A);
            Assert.InRange(p.R, 0, p.A); Assert.InRange(p.G, 0, p.A); Assert.InRange(p.B, 0, p.A);
        }
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 0).R).Distinct().Count());
        stroke.Working.Dispose();
    }

    [Fact]
    public void Low_opacity_brush_changes_less_than_one_display_level_but_more_than_one_16_bit_level()
    {
        using var source = Ramp();
        using var stroke = new BrushStroke(source, new() { Size = 20, Hardness = 1, PressureSize = false, Opacity = .0001 }, BrushMode.Paint, SKColors.White);
        stroke.AddPoint(new(240.5f, 32.5f));
        var delta = new PixelBuffer(stroke.Working).Read(240, 32).R - new PixelBuffer(source).Read(240, 32).R;
        Assert.InRange(delta, 1 / 65535f, 1 / 255f);
        stroke.Working.Dispose();
    }

    [Fact]
    public void Session_selection_fill_growth_clipboard_resize_and_undo_keep_source_precision()
    {
        using var source = Ramp();
        var document = new Document(512, 64) { Depth = ColorDepth.Sixteen };
        document.InsertAboveActive(Layer.Raster("Fine gradient", source));
        var session = new EditorSession(document);
        session.SelectRect(new(220, 20, 280, 40));
        session.Fill(SKColors.Red);
        Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer!.Pixels!.ColorType);
        Assert.Equal(new PixelBuffer(source).Read(10, 10), new PixelBuffer(session.ActiveLayer.Pixels).Read(10, 10));
        session.Undo();
        Assert.Same(source, session.ActiveLayer!.Pixels);
        session.Deselect();
        session.CopyMerged(whole: true);
        Assert.Equal(SKColorType.RgbaF32, EditorSession.Clipboard!.Pixels.ColorType);
        session.ResizeImage(1024, 128);
        var resized = session.ActiveLayer!.Pixels!;
        Assert.Equal(SKColorType.RgbaF32, resized.ColorType);
        var pixels = new PixelBuffer(resized);
        Assert.True(Enumerable.Range(10, 1000).Select(x => pixels.Read(x, 64).R).Distinct().Count() > 900);
        session.Undo(); Assert.Same(source, session.ActiveLayer!.Pixels);
        session.ResizeCanvas(540, 80, Anchor.Center);
        session.Foreground = SKColors.Blue;
        session.Brush = new() { Size = 4, Hardness = 1, PressureSize = false };
        Assert.True(session.BeginStroke(new(2, 2), out _)); session.EndStroke();
        Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer!.Pixels!.ColorType);
        session.Undo();
        Assert.Same(source, session.ActiveLayer!.Pixels);
    }

    [Fact]
    public void Trim_does_not_throw_away_alpha_smaller_than_one_display_step()
    {
        using var bitmap = Pixels.NewColor(40, 30, ColorDepth.Sixteen);
        new PixelBuffer(bitmap).Write(17, 12, new(0, 0, 0, 1 / 65535f));
        Pixels.Invalidate(bitmap);
        Assert.Equal(new SKRectI(17, 12, 18, 13), ImageTrim.Bounds(bitmap, new TrimOptions()));
    }
}

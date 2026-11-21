using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class CaptionSelectionTests
{
    [Fact]
    public void A_click_follows_connected_thin_strokes_and_leaves_counters_and_other_letters_empty()
    {
        using var image = Pixels.NewColor(230, 130);
        image.Erase(SKColors.Black);
        using var canvas = new SKCanvas(image);
        using var pen = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
        canvas.DrawOval(new SKRect(12, 20, 98, 105), pen);
        canvas.DrawOval(new SKRect(58, 20, 144, 105), pen);
        canvas.DrawOval(new SKRect(160, 20, 218, 105), pen);
        using var strokes = GraphicSelection.AtPoint(image, new(12, 62), default);
        Assert.NotNull(strokes);
        Assert.True(strokes.GetPixel(143, 62).Alpha >= 128);
        Assert.Equal(0, strokes.GetPixel(80, 62).Alpha);
        Assert.Equal(0, strokes.GetPixel(160, 62).Alpha);
        Assert.Contains(strokes.GetPixelSpan().ToArray(), a => a is > 0 and < 255);
        Assert.Null(GraphicSelection.AtPoint(image, new(80, 62), default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Display_type_distinguishes_an_image_clipped_into_strokes_from_an_occluding_object(bool clipped)
    {
        using var image = Pixels.NewColor(244, 200);
        image.Erase(SKColors.DarkRed);
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint();
        for (int x = 18; x < 230; x += 43)
        {
            paint.Color = SKColors.Ivory;
            canvas.DrawRect(x, 15, 35, 169, paint);
            if (clipped)
            {
                paint.Color = SKColors.DarkSlateBlue;
                canvas.DrawRect(x, 78, 35, 101, paint);
            }
            paint.Color = SKColors.DarkRed;
            canvas.DrawRect(x + 12, 32, 6, 122, paint);
        }
        if (!clipped)
        {
            paint.Color = SKColors.DarkSlateBlue;
            canvas.DrawRect(15, 85, 214, 50, paint);
        }
        using var selection = DisplayLettering.Repair(image, new(12, 8, 232, 191), null, default);
        Assert.NotNull(selection);
        for (int x = 18; x < 230; x += 43)
        {
            Assert.Equal(255, selection.GetPixel(x + 4, 35).Alpha);
            Assert.Equal(clipped ? 255 : 0, selection.GetPixel(x + 4, 99).Alpha);
            Assert.Equal(0, selection.GetPixel(x + 15, 99).Alpha);
            Assert.Equal(0, selection.GetPixel(x + 37, 99).Alpha);
            Assert.Equal(255, selection.GetPixel(x + 5, 164).Alpha);
        }
    }

    [Fact]
    public void A_wide_frame_can_find_image_filled_letters_across_a_poster_margin()
    {
        using var image = Pixels.NewColor(400, 340);
        image.Erase(SKColors.Black);
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { Color = SKColors.DarkRed };
        canvas.DrawRect(20, 20, 360, 300, paint);
        for (int x = 51; x < 350; x += 50)
        {
            paint.Color = SKColors.Ivory;
            canvas.DrawRect(x, 62, 42, 214, paint);
            paint.Color = SKColors.DarkSlateBlue;
            canvas.DrawRect(x, 142, 42, 134, paint);
            paint.Color = SKColors.DarkRed;
            canvas.DrawRect(x + 16, 78, 7, 175, paint);
        }
        using var selection = DisplayLettering.Repair(image, new(2, 2, 396, 338), null, default);
        Assert.NotNull(selection);
        for (int x = 51; x < 350; x += 50)
        {
            Assert.Equal(255, selection.GetPixel(x + 5, 72).Alpha);
            Assert.Equal(255, selection.GetPixel(x + 5, 180).Alpha);
            Assert.Equal(0, selection.GetPixel(x + 19, 180).Alpha);
            Assert.Equal(0, selection.GetPixel(x + 45, 180).Alpha);
        }
    }

    [Fact]
    public void Inclined_line_is_measured_in_its_own_direction_and_rasterized_on_the_original_grid()
    {
        using var image = Pixels.NewColor(240, 140);
        image.Erase(SKColors.DarkSlateGray);
        using var c = new SKCanvas(image);
        using var brush = new SKPaint { Color = SKColors.White };
        c.Skew(0, -.24f);
        foreach (int x in new[] { 26, 57, 84, 113, 148, 183 })
        {
            int width = x % 2 == 0 ? 22 : 14;
            c.DrawRect(x, 86, width, 3, brush);
            c.DrawRect(x, 86, 4, 20, brush);
            c.DrawRect(x, 103, width, 3, brush);
        }
        using var plate = SelectionMask.FromRect(240, 140, new(4, 20, 230, 124));
        using var text = CaptionSelection.Recover(image, plate, new(15, 35, 210, 108), default);
        Assert.NotNull(text);
        Assert.Equal(255, text.GetPixel(27, 85).Alpha);
        Assert.Equal(255, text.GetPixel(184, 50).Alpha);
        Assert.Equal(0, text.GetPixel(175, 55).Alpha);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Box_around_caption_keeps_strokes_counters_and_spaces(bool darkInk)
    {
        using var image = Pixels.NewColor(210, 64);
        image.Erase(darkInk ? SKColors.WhiteSmoke : SKColors.DarkSlateGray);
        using var c = new SKCanvas(image);
        using var paint = new SKPaint { Color = darkInk ? SKColors.DarkBlue : SKColors.White };
        foreach (var (x, width) in new[] { (19, 16), (47, 21), (80, 12), (105, 19), (135, 24), (170, 13) })
        {
            c.DrawRect(x, 24, width, 4, paint); c.DrawRect(x, 40, width, 4, paint);
            c.DrawRect(x, 24, 4, 20, paint); c.DrawRect(x + width - 4, 24, 4, 20, paint);
        }
        image.SetPixel(16, 31, darkInk ? SKColors.White : new SKColor(34, 66, 66));
        var gesture = new SKRectI(13, 17, 191, 50);
        using var joined = SelectionMask.FromRect(210, 64, new(17, 22, 185, 46));
        Assert.Null(CaptionSelection.Recover(image, joined, gesture, default, joinedOnly: true));
        using var recovered = CaptionSelection.Recover(image, joined, gesture, default);
        Assert.NotNull(recovered);
        Assert.Equal(255, recovered.GetPixel(20, 33).Alpha);
        Assert.Equal(0, recovered.GetPixel(26, 33).Alpha);
        Assert.Equal(0, recovered.GetPixel(41, 33).Alpha);
        Assert.InRange(recovered.GetPixel(17, 31).Alpha, (byte)0, (byte)4);
        Assert.Equal(255, joined.GetPixel(26, 33).Alpha);
        Assert.Null(CaptionSelection.Recover(image, recovered, gesture, default));
        // When the user frames the entire labelled object, retain its outer shape.
        using var sign = SelectionMask.FromRect(210, 64, new(8, 11, 196, 53));
        Assert.Null(CaptionSelection.Recover(image, sign, new(3, 7, 201, 58), default));
    }
}

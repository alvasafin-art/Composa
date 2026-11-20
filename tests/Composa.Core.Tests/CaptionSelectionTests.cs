using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class CaptionSelectionTests
{
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

using Composa.Editing;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SelectionBrushDamageTests
{
    [Theory]
    [InlineData(SelectionMode.Replace)]
    [InlineData(SelectionMode.Add)]
    [InlineData(SelectionMode.Subtract)]
    [InlineData(SelectionMode.Intersect)]
    public void Local_updates_match_full_mask_combine_including_feather_and_repeated_passes(SelectionMode mode)
    {
        var session = EditorSession.NewCanvas(427, 233);
        session.SelectRect(new(48, 27, 305, 162));
        session.SelectionBrushSize = 53;
        session.SelectionBrushFeather = 17;
        var basis = session.Selection!;
        var original = basis.Bytes;
        using var reference = Pixels.NewMask(basis.Width, basis.Height);
        SKPoint[] path = [new(-5, 47), new(112, 50), new(267, 129), new(112, 50), new(451, 220)];
        using var feather = SKImageFilter.CreateBlur(8.5f, 8.5f);
        using var paint = new SKPaint
        {
            StrokeWidth = 53, StrokeCap = SKStrokeCap.Round, IsAntialias = true, ImageFilter = feather
        };
        for (var i = 0; i < path.Length; i++)
        {
            var point = path[i];
            using (var canvas = new SKCanvas(reference))
            {
                paint.Style = SKPaintStyle.Stroke;
                canvas.DrawLine(path[Math.Max(0, i - 1)], point, paint);
                if (i == 0)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(point, 26.5f, paint);
                }
            }
            if (i == 0) session.BeginSelectionBrush(point, mode);
            else session.ContinueSelectionBrush(point);
            var combined = SelectionMask.Combine(basis, reference, mode);
            Assert.Equal(combined?.Bytes, session.Selection?.Bytes);
            if (!ReferenceEquals(combined, reference)) combined?.Dispose();
        }
        session.EndSelectionBrush();
        Assert.Equal(original, basis.Bytes);
        session.Undo();
        Assert.Same(basis, session.Selection);
    }

    [Fact]
    public void Subtract_can_erase_the_last_pixel_and_cancel_restores_the_original_mask()
    {
        var session = EditorSession.NewCanvas(307, 193);
        session.SelectRect(new(141, 89, 161, 108));
        var basis = session.Selection;
        session.BeginSelectionBrush(new(150, 99), SelectionMode.Subtract);
        Assert.Null(session.Selection);
        session.CancelSelectionBrush();
        Assert.Same(basis, session.Selection);
    }
}

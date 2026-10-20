using Composa.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SceneBackgroundSubjectTests
{
    [Fact]
    public void Actual_object_extends_outside_original_but_disconnected_neighbors_are_excluded()
    {
        using var original = Pixels.NewMask(200, 140); using var candidate = Pixels.NewMask(200, 140);
        Paint(original, new(70, 50, 100, 100)); Paint(candidate, new(65, 45, 140, 110)); Paint(candidate, new(10, 20, 35, 100)); Paint(candidate, new(160, 50, 190, 100));
        var bytes = candidate.Bytes; using var found = SceneBackgroundSubject.Anchored(candidate, original, 128, CancellationToken.None);
        Assert.Equal(255, found.GetPixel(135, 105).Alpha); Assert.Equal(255, found.GetPixel(75, 65).Alpha); Assert.Equal(0, found.GetPixel(20, 60).Alpha); Assert.Equal(0, found.GetPixel(170, 60).Alpha); Assert.Equal(bytes, candidate.Bytes);
    }

    [Fact]
    public void Cleanup_unions_original_and_actual_without_erasing_holes_or_neighbors()
    {
        var session = EditorSession.NewCanvas(320, 220, SKColors.Beige); var product = Pixels.NewColor(80, 100);
        using (var draw = new SKCanvas(product)) using (var paint = new SKPaint { Color = SKColors.Red }) { draw.DrawRect(0, 0, 14, 100, paint); draw.DrawRect(66, 0, 14, 100, paint); draw.DrawRect(0, 86, 80, 14, paint); }
        Pixels.Invalidate(product); session.AddImageLayer("PNG", product, new(140, 100), fit: false);
        using var input = SceneEditInput.Prepare(session.Document.Clone(), new() { Task = AiTaskKind.ChangeBackground });
        using var actual = Pixels.NewMask(320, 220); Paint(actual, new(170, 55, 230, 150));
        using var plate = new SceneBackgroundPlate(input, input.Before, 8, CancellationToken.None, actual);
        Assert.Equal(255, plate.Mask.GetPixel(220, 90).Alpha); Assert.Equal(255, plate.Mask.GetPixel(107, 90).Alpha);
        Assert.Equal(0, plate.Mask.GetPixel(140, 80).Alpha); Assert.Equal(0, plate.Mask.GetPixel(280, 90).Alpha); Assert.Equal(input.Before.Bytes, plate.Image.Bytes);
    }

    [Fact]
    public void Mismatched_planes_and_cancellation_leave_inputs_untouched()
    {
        using var a = Pixels.NewMask(40, 30); using var b = Pixels.NewMask(41, 30);
        Assert.Throws<ArgumentException>(() => SceneBackgroundSubject.Anchored(a, b, 128, CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); Assert.Throws<OperationCanceledException>(() => SceneBackgroundSubject.Anchored(a, a, 128, canceled.Token));
        Assert.All(a.Bytes, value => Assert.Equal(0, value));
    }
    private static void Paint(SKBitmap mask, SKRect rect) { using (var draw = new SKCanvas(mask)) using (var paint = new SKPaint { Color = SKColors.White }) draw.DrawRect(rect, paint); Pixels.Invalidate(mask); }
}

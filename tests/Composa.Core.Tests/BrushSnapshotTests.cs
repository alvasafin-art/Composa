using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class BrushSnapshotTests
{
    [Theory]
    [InlineData(2051, 2049, ColorDepth.Eight, 29)]
    [InlineData(1051, 1049, ColorDepth.Sixteen, 29)]
    [InlineData(2051, 2049, ColorDepth.Eight, 480)]
    [InlineData(1051, 1049, ColorDepth.Sixteen, 480)]
    public void LargeStrokeMatchesIndependentCopyDuringDrawingAndAcrossHistory(int w, int h, ColorDepth depth, int brushSize)
    {
        var session = EditorSession.NewCanvas(w, h, SKColors.DarkSlateGray, depth);
        session.Tool = Tool.Brush;
        session.Brush = new() { Size = brushSize, PressureFlow = true, Flow = .65, Opacity = .73 };
        session.Foreground = SKColors.OrangeRed;
        SKBitmap Current() => session.ActiveLayer!.Pixels!;
        var original = Current();
        using var originalCopy = Pixels.Clone(original);

        SKBitmap Draw(SKBitmap basis, SKPoint start, SKPoint end, BrushMode mode)
        {
            using var reference = new BrushStroke(basis, session.Brush, mode, session.Foreground);
            reference.AddPoint(start, .6f);
            Assert.True(session.BeginStroke(start, out _, mode: mode, pressure: .6f));
            EqualPixels(reference.Working, Current());
            reference.AddPoint(end, .9f);
            session.ContinueStroke(end, .9f);
            EqualPixels(reference.Working, Current());
            session.EndStroke();
            EqualPixels(reference.Working, Current());
            return reference.Working;
        }

        using var first = Draw(originalCopy, new(127, 126), new(415, 419), BrushMode.Paint);
        using var second = Draw(first, new(w - 3, h - 2), new(w - 31, h - 13), BrushMode.Erase);
        session.Undo();
        EqualPixels(first, Current());
        session.Redo();
        EqualPixels(second, Current());
        session.Undo();
        var saved = Current();
        Assert.True(session.BeginStroke(new(800, 500), out _));
        session.ContinueStroke(new(888, 530));
        session.CancelStroke();
        EqualPixels(first, Current());
        using var branch = Draw(first, new(64, h - 1), new(192, h - 1), BrushMode.Paint);
        Assert.False(session.CanRedo);
        EqualPixels(first, saved);
        session.Undo();
        session.Undo();
        EqualPixels(originalCopy, Current());
        EqualPixels(originalCopy, original);
        session.Redo();
        session.Redo();
        EqualPixels(branch, Current());
    }

    [Theory]
    [InlineData(ColorDepth.Eight, BrushMode.Paint, false)]
    [InlineData(ColorDepth.Sixteen, BrushMode.Paint, false)]
    [InlineData(ColorDepth.Eight, BrushMode.Erase, true)]
    public void Fast_segment_matches_the_same_dabs_delivered_one_at_a_time(ColorDepth depth, BrushMode mode, bool mask)
    {
        using var original = mask ? Pixels.NewMask(800, 800, 208) : Pixels.NewColor(800, 800, depth);
        if (!mask) original.Erase(SKColors.DarkSlateGray);
        using var selection = Pixels.NewMask(800, 800, 179);
        var settings = new BrushSettings { Size = 80, Spacing = .0625, PressureSize = false, PressureFlow = true, Flow = .67, Opacity = .83, Hardness = .27 };
        using var batched = new BrushStroke(original, settings, mode, SKColors.OrangeRed) { Selection = selection };
        using var individual = new BrushStroke(original, settings, mode, SKColors.OrangeRed) { Selection = selection };
        using var actual = batched.Working;
        using var expected = individual.Working;
        var start = new SKPoint(167.5f, 108.5f);
        batched.AddPoint(start, .25f);
        individual.AddPoint(start, .25f);

        // Each (3, 4) step is exactly one 5 px spacing. The full path exceeds one batch.
        batched.AddPoint(start + new SKPoint(384, 512), .75f);
        for (var i = 1; i <= 128; i++)
            individual.AddPoint(start + new SKPoint(3 * i, 4 * i), .25f + i / 256f);

        EqualPixels(expected, actual);
        Assert.False(original.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Pointer_samples_preserve_turns_pressure_and_smoothing_when_delivered_together(ColorDepth depth)
    {
        var sequential = EditorSession.NewCanvas(800, 700, SKColors.DarkSlateGray, depth);
        var combined = EditorSession.NewCanvas(800, 700, SKColors.DarkSlateGray, depth);
        var settings = new BrushSettings { Size = 180, Hardness = .73, Flow = .62, Opacity = .83, PressureFlow = true, Smoothing = 9 };
        var first = new SKPoint(94, 86);
        foreach (var session in new[] { sequential, combined })
        {
            session.ViewZoom = .33;
            session.Brush = settings;
            session.Foreground = SKColors.OrangeRed;
            Assert.True(session.BeginStroke(first, out var problem, mode: BrushMode.Paint, pressure: .6f), problem);
        }

        (SKPoint Point, float Pressure)[] samples =
        [
            (new(98, 88), .62f), (new(240, 226), .74f), (new(258, 234), .88f), (new(514, 152), 1),
            (new(594, 346), .75f), (new(178, 574), .65f), (new(110, 315), .47f)
        ];
        foreach (var (point, pressure) in samples) sequential.ContinueStroke(point, pressure);
        combined.ContinueStrokeSamples(samples);
        Assert.Equal(sequential.BrushPosition, combined.BrushPosition);
        Assert.Equal(sequential.BrushPressure, combined.BrushPressure);
        EqualPixels(sequential.ActiveLayer!.Pixels!, combined.ActiveLayer!.Pixels!);

        sequential.EndStroke();
        combined.EndStroke();
        EqualPixels(sequential.ActiveLayer!.Pixels!, combined.ActiveLayer!.Pixels!);
        var end = combined.ActiveLayer.Pixels!;
        combined.Undo();
        Assert.NotSame(end, combined.ActiveLayer!.Pixels);
        combined.Redo();
        Assert.Same(end, combined.ActiveLayer!.Pixels);
    }

    [Fact]
    public void MaskForksSurviveReleasedAncestorsAndUntrackedEdits()
    {
        const int size = 4097;
        using var root = Pixels.NewLayerMask(size, size, 77);
        using var first = LayerPixelStorage.CopyForStroke(root);
        first.GetPixelSpan()[first.RowBytes * (size - 1) + size - 1] = 19;
        Pixels.Invalidate(first, new SKRectI(size - 1, size - 1, size + 20, size + 20));
        using var branch = LayerPixelStorage.CopyForStroke(first);
        Assert.Equal(77, root.GetPixelSpan()[^1]);
        first.Dispose();
        root.Dispose();
        Assert.Equal(19, branch.GetPixelSpan()[^1]);
        Assert.Equal(77, branch.GetPixelSpan()[0]);
        branch.Erase(new SKColor(0, 0, 0, 153));
        Pixels.Invalidate(branch);
        using var rebased = LayerPixelStorage.CopyForStroke(branch);
        EqualPixels(branch, rebased);
        using var next = LayerPixelStorage.CopyForStroke(rebased);
        next.GetPixelSpan()[0] = 45;
        Assert.Equal(153, rebased.GetPixelSpan()[0]);
    }

    [Fact]
    public void DenseAndPaddedInputsPreserveEveryRowWhenRebased()
    {
        var info = Pixels.ColorInfo(2051, 2049);
        using var padded = new SKBitmap();
        Assert.True(padded.TryAllocPixels(info, info.RowBytes + 64));
        padded.Erase(SKColors.DarkOliveGreen);
        using var root = LayerPixelStorage.CopyForStroke(padded);
        for (int y = 0; y < info.Height; y++)
            Assert.True(padded.GetPixelSpan().Slice(y * padded.RowBytes, info.RowBytes)
                .SequenceEqual(root.GetPixelSpan().Slice(y * root.RowBytes, info.RowBytes)));
        using var dense = LayerPixelStorage.CopyForStroke(root);
        dense.Erase(SKColors.Gold);
        Pixels.Invalidate(dense, new SKRectI(0, 0, info.Width, info.Height));
        using var next = LayerPixelStorage.CopyForStroke(dense);
        EqualPixels(dense, next);
        Assert.Equal(SKColors.DarkOliveGreen, root.GetPixel(0, 0));
    }

    private static void EqualPixels(SKBitmap expected, SKBitmap actual) =>
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "Bitmap contents differ.");
}

using System.Diagnostics;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class InpaintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_print_uses_an_intact_row_and_keeps_the_shape_of_the_ink(bool spot)
    {
        using var clean = Pixels.NewColor(200, 160);
        var random = new Random(210);
        for (var y = 0; y < clean.Height; y++)
        for (var x = 0; x < clean.Width; x++)
        {
            int column = x % 23, row = y % 37;
            bool ink = row is >= 8 and <= 24 && (column is 4 or 5 or 13 or 14 || row is 14 or 15 && column is >= 6 and <= 12);
            var value = (byte)(ink ? 26 : 185 + random.Next(22));
            clean.SetPixel(x, y, new SKColor(value, value, value));
        }
        using var source = Pixels.Clone(clean); using var mask = Pixels.NewMask(200, 160);
        var area = new SKRectI(78, 70, 129, 99); Hole(source, mask, area);
        using var result = Inpaint.Fill(source, mask, coherentSpot: spot);
        AssertOutsideUnchanged(source, mask, result);
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            Assert.Equal(clean.GetPixel(x, y).Red < 90, result.GetPixel(x, y).Red < 90);
    }

    [Fact]
    public void A_thin_scratch_preserves_antialiased_diagonal_ink_edges()
    {
        using var clean = Pixels.NewColor(80, 40); clean.Erase(SKColors.White);
        using (var canvas = new SKCanvas(clean))
        using (var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 5, IsAntialias = true })
            foreach (var x in new[] { 10, 27, 52 }) canvas.DrawLine(x, 0, x + 14, 40, paint);
        using var dirty = Pixels.Clone(clean); using var mask = Pixels.NewMask(80, 40);
        Hole(dirty, mask, new SKRectI(5, 18, 73, 23));
        using var result = Inpaint.Fill(dirty, mask);
        Assert.True(Error(clean, result, mask) < 9);
        AssertOutsideUnchanged(dirty, mask, result);
    }

    [Fact]
    public void Feathered_fill_applies_coverage_once_across_a_fragmented_print()
    {
        using var source = Pattern(180, 100);
        using var hard = Pixels.NewMask(180, 100);
        Hole(source, hard, new SKRectI(12, 12, 24, 88)); Hole(source, hard, new SKRectI(151, 12, 163, 88));
        using var soft = Pixels.Clone(hard);
        for (var y = 12; y < 88; y++) soft.GetPixelSpan()[soft.RowBytes * y + 12] = 128;
        Pixels.Invalidate(soft);
        using var full = Inpaint.Fill(source, hard);
        using var feathered = Inpaint.Fill(source, soft);
        for (var y = 12; y < 88; y++)
        {
            int expected = (source.GetPixel(12, y).Red * 127 + full.GetPixel(12, y).Red * 128 + 127) / 255;
            Assert.InRange(Math.Abs(feathered.GetPixel(12, y).Red - expected), 0, 1);
        }
    }

    [Fact]
    public void Separate_blemishes_keep_local_texture_and_do_not_borrow_a_distant_object()
    {
        using var clean = Pattern(180, 100);
        using var source = Pixels.Clone(clean);
        using var mask = Pixels.NewMask(180, 100);
        Hole(source, mask, new SKRectI(12, 12, 24, 88));
        Hole(source, mask, new SKRectI(151, 12, 163, 88));
        using var result = Inpaint.Fill(source, mask);
        AssertOutsideUnchanged(source, mask, result);
        Assert.True(Error(clean, result, mask) < 20, $"texture error: {Error(clean, result, mask):0.0}");
        Assert.Equal(SKColors.Magenta, source.GetPixel(17, 47)); // Input is immutable.
    }

    [Fact]
    public void A_thin_diagonal_feature_is_continued_through_a_hole()
    {
        using var clean = Pixels.NewColor(112, 112); clean.Erase(SKColors.LightGray);
        using (var canvas = new SKCanvas(clean))
        using (var paint = new SKPaint { Color = SKColors.DarkSlateGray, StrokeWidth = 5, IsAntialias = false })
            canvas.DrawLine(0, 0, 111, 111, paint);
        using var source = Pixels.Clone(clean);
        using var mask = Pixels.NewMask(112, 112);
        Hole(source, mask, new SKRectI(48, 48, 64, 64));
        using var result = Inpaint.Fill(source, mask);
        AssertOutsideUnchanged(source, mask, result);
        Assert.True(result.GetPixel(56, 56).Red < 130, $"broken line: {result.GetPixel(56, 56)}");
        Assert.True(result.GetPixel(49, 62).Red > 160);
    }

    [Fact]
    public void Soft_masks_blend_once_and_results_are_reproducible()
    {
        using var source = Pixels.NewColor(90, 80); source.Erase(new SKColor(80, 140, 190));
        using var mask = Pixels.NewMask(90, 80);
        Hole(source, mask, new SKRectI(38, 33, 52, 47));
        mask.SetPixel(37, 40, new SKColor(0, 0, 0, 128)); source.SetPixel(37, 40, SKColors.Black);
        using var a = Inpaint.Fill(source, mask); using var b = Inpaint.Fill(source, mask);
        Assert.True(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()));
        AssertOutsideUnchanged(source, mask, a);
        Assert.InRange(a.GetPixel(37, 40).Red, (byte)37, (byte)43);
        Assert.Equal((byte)255, a.GetPixel(45, 40).Alpha);
    }

    [Fact]
    public void No_source_pixels_leaves_the_original_intact_instead_of_inventing_a_fill()
    {
        using var source = Pattern(32, 24);
        using var mask = Pixels.NewMask(32, 24, 255);
        using var result = Inpaint.Fill(source, mask);
        Assert.True(source.GetPixelSpan().SequenceEqual(result.GetPixelSpan()));
    }

    [Fact]
    public void Healing_and_fill_use_the_active_layer_and_undo_exactly()
    {
        var session = EditorSession.NewCanvas(90, 80, SKColors.Green);
        var layer = session.AddImageLayer("photo", Pattern(90, 80), fit: false);
        session.SelectRect(new SKRect(38, 33, 52, 47)); session.Fill(SKColors.Magenta);
        var before = session.ActiveLayer!.Pixels!;
        session.ContentAwareFill();
        Assert.NotSame(before, session.ActiveLayer.Pixels);
        session.Undo(); Assert.Same(before, session.ActiveLayer!.Pixels);
        session.Deselect(); session.Tool = Tool.SpotHealing; session.Brush = new BrushSettings { Size = 26, Hardness = 1 };
        session.BeginStroke(new SKPoint(45, 40), out var problem); Assert.Null(problem); session.EndStroke();
        Assert.True(session.ActiveLayer.Pixels!.GetPixel(45, 40).Green < 150);
        session.Undo(); Assert.Same(before, session.ActiveLayer!.Pixels);
    }

    [Fact]
    public void Local_heal_on_a_large_photo_does_not_scan_the_whole_canvas()
    {
        using var source = Pixels.NewColor(2200, 1400); source.Erase(SKColors.CornflowerBlue);
        using var mask = Pixels.NewMask(2200, 1400);
        Hole(source, mask, new SKRectI(1090, 690, 1110, 710));
        var timer = Stopwatch.StartNew();
        using var result = Inpaint.Fill(source, mask);
        Assert.InRange(result.GetPixel(1100, 700).Red, (byte)97, (byte)103);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), $"local heal took {timer.Elapsed}");
    }

    [Fact]
    public void Large_guides_keep_native_texture_and_semitransparent_output_stays_premultiplied()
    {
        using var clean = Pattern(1100, 500);
        using var source = Pixels.Clone(clean); using var mask = Pixels.NewMask(1100, 500);
        // Neither a full-width nor a full-height donor rectangle can fit. Matching must use small patches.
        Hole(source, mask, new SKRectI(10, 8, 36, 492)); Hole(source, mask, new SKRectI(1064, 8, 1090, 492));
        var timer = Stopwatch.StartNew(); using var result = Inpaint.Fill(source, mask);
        AssertOutsideUnchanged(source, mask, result);
        // Working-guide reduction must not turn the high-frequency pattern into a flat patch.
        var colors = new HashSet<byte>();
        for (var y = 30; y < 450; y++) colors.Add(result.GetPixel(20, y).Red);
        Assert.True(colors.Max() - colors.Min() > 40);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), $"large fill took {timer.Elapsed}");

        using var translucent = Pixels.NewColor(60, 50); translucent.Erase(new SKColor(80, 120, 170, 128));
        using var softMask = Pixels.NewMask(60, 50);
        Hole(translucent, softMask, new SKRectI(22, 17, 38, 33));
        using var healed = Inpaint.Fill(translucent, softMask);
        var bytes = healed.GetPixelSpan();
        for (var i = 0; i < bytes.Length; i += 4)
            Assert.True(bytes[i] <= bytes[i + 3] && bytes[i + 1] <= bytes[i + 3] && bytes[i + 2] <= bytes[i + 3]);
    }

    [Fact]
    public void Native_fill_visual_comparison_covers_disjoint_texture_and_a_gradient_scratch()
    {
        using var clean = Pattern(320, 180);
        for (var y = 0; y < 180; y++) for (var x = 160; x < 320; x++)
            clean.SetPixel(x, y, new SKColor((byte)(130 + x / 8), (byte)(140 + y / 8), 180));
        using var source = Pixels.Clone(clean); using var mask = Pixels.NewMask(320, 180);
        Hole(source, mask, new SKRectI(12, 12, 25, 168)); Hole(source, mask, new SKRectI(115, 12, 128, 168));
        Hole(source, mask, new SKRectI(209, 77, 249, 103));
        using var result = Inpaint.Fill(source, mask);
        AssertOutsideUnchanged(source, mask, result);
        Assert.True(Error(clean, result, mask) < 24);
        using var comparison = Pixels.NewColor(960, 208); comparison.Erase(SKColors.White);
        using (var canvas = new SKCanvas(comparison))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        using (var font = new SKFont(SKTypeface.Default, 16))
        {
            canvas.DrawText("Original", 8, 20, font, paint); canvas.DrawText("Damaged", 328, 20, font, paint); canvas.DrawText("Content-Aware Fill", 648, 20, font, paint);
            canvas.DrawImage(Pixels.ImageOf(clean), 0, 28); canvas.DrawImage(Pixels.ImageOf(source), 320, 28); canvas.DrawImage(Pixels.ImageOf(result), 640, 28);
        }
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(folder); ImageFiles.Save(comparison, Path.Combine(folder, "native-fill-comparison.png"), ExportFormat.Png);
    }

    [Fact]
    public void Reduced_guide_never_leaves_selected_specks_or_original_scratch_pixels()
    {
        using var source = Pixels.NewColor(900, 620); source.Erase(new SKColor(80, 140, 190));
        using var mask = Pixels.NewMask(900, 620);
        // Irregular edges and isolated one-pixel repairs cross the guide's rounding boundaries.
        for (var y = 80; y < 540; y++) for (var x = 200 + y % 3; x < 220 + y % 7; x++)
        { source.SetPixel(x, y, SKColors.Magenta); mask.SetPixel(x, y, new SKColor(0, 0, 0, 255)); }
        for (var y = 81; y < 539; y += 17)
        { source.SetPixel(228, y, SKColors.Magenta); mask.SetPixel(228, y, new SKColor(0, 0, 0, 255)); }
        Pixels.Invalidate(source); Pixels.Invalidate(mask);
        using var result = Inpaint.Fill(source, mask);
        AssertOutsideUnchanged(source, mask, result);
        for (var y = 0; y < 620; y++) for (var x = 0; x < 900; x++)
            if (mask.GetPixel(x, y).Alpha == 255)
            {
                Assert.InRange(result.GetPixel(x, y).Red, (byte)77, (byte)83);
                Assert.InRange(result.GetPixel(x, y).Green, (byte)137, (byte)143);
            }
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Irregular_hole_keeps_a_slanted_material_boundary_and_ignores_excluded_colors(ColorDepth depth)
    {
        const int width = 140, height = 100;
        using var clean = Pixels.NewColor(width, height, depth);
        var random = new Random(79);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int tone = x > 85 - y * .43 ? 48 : 164;
            int texture = random.Next(-7, 8) + (int)(4 * Math.Sin(x * .26 + y * .12));
            clean.SetPixel(x, y, new SKColor((byte)(tone + texture + 10), (byte)(tone + texture), (byte)(tone + texture - 6)));
        }
        using var mask = Pixels.NewMask(width, height);
        using (var canvas = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawOval(new SKRect(42, 36, 96, 65), paint);
        using var dirty = Pixels.Clone(clean);
        using var other = Pixels.Clone(clean);
        using var avoid = Pixels.NewMask(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (mask.GetPixel(x, y).Alpha > 0)
            {
                dirty.SetPixel(x, y, SKColors.Magenta);
                other.SetPixel(x, y, SKColors.Blue);
            }
            else if (x is >= 32 and < 45 && y is >= 28 and < 68)
            {
                dirty.SetPixel(x, y, SKColors.Yellow);
                other.SetPixel(x, y, SKColors.Red);
                avoid.SetPixel(x, y, SKColors.White);
            }
        }
        using var first = Inpaint.Fill(dirty, mask, avoid);
        using var second = Inpaint.Fill(other, mask, avoid);
        AssertOutsideUnchanged(dirty, mask, first);
        Assert.Equal(depth, Pixels.DepthOf(first));
        Assert.True(Error(clean, first, mask) < 10);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (mask.GetPixel(x, y).Alpha == 255)
                Assert.Equal(first.GetPixel(x, y), second.GetPixel(x, y));
    }

    private static SKBitmap Pattern(int width, int height)
    {
        var image = Pixels.NewColor(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var stripe = (x + y) % 8 < 4 ? 44 : 110;
            image.SetPixel(x, y, new SKColor((byte)stripe, (byte)(stripe + 25), (byte)(stripe + 45)));
        }
        return image;
    }

    private static void Hole(SKBitmap image, SKBitmap mask, SKRectI rect)
    {
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        { image.SetPixel(x, y, SKColors.Magenta); mask.SetPixel(x, y, new SKColor(0, 0, 0, 255)); }
    }

    private static void AssertOutsideUnchanged(SKBitmap source, SKBitmap mask, SKBitmap result)
    {
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
            if (mask.GetPixel(x, y).Alpha == 0) Assert.Equal(source.GetPixel(x, y), result.GetPixel(x, y));
    }

    private static double Error(SKBitmap clean, SKBitmap result, SKBitmap mask)
    {
        double error = 0; var count = 0;
        for (var y = 0; y < clean.Height; y++)
        for (var x = 0; x < clean.Width; x++)
        {
            if (mask.GetPixel(x, y).Alpha != 255) continue;
            error += Math.Abs(clean.GetPixel(x, y).Red - result.GetPixel(x, y).Red); count++;
        }
        return error / Math.Max(1, count);
    }
}

using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class AlphaMattingTests
{
    [Fact]
    public void Uniform_neutral_backdrop_is_removed_from_translucent_fur_on_both_new_backgrounds()
    {
        using var image = Pixels.NewColor(96, 48);
        using var mask = Pixels.NewMask(96, 48);
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 96; x++)
            {
                float alpha = Math.Clamp((72 - x) / 40f, 0, 1);
                byte tone = (byte)MathF.Round(60 * alpha + 220 * (1 - alpha));
                image.SetPixel(x, y, new SKColor(tone, tone, tone));
                mask.GetPixelSpan()[y * mask.RowBytes + x] = (byte)MathF.Round(alpha * 255);
            }
        using var cutout = MaskRefinement.Cutout(image, mask, 1);
        var pixel = new PixelBuffer(cutout).Read(52, 24);
        foreach (float backdrop in new[] { 0f, 1f })
            Assert.InRange(Math.Abs(pixel.R + backdrop * (1 - pixel.A) - (30f / 255 + .5f * backdrop)), 0, .01f);
    }

    [Fact]
    public void A_soft_matte_on_a_textured_backdrop_does_not_inherit_nearest_colour_cells()
    {
        using var image = Pixels.NewColor(80, 40);
        using var matte = Pixels.NewMask(80, 40);
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 80; x++)
            {
                byte tone = x < 20 ? (byte)(y < 20 ? 240 : 160) : x >= 60 ? (byte)(y < 17 ? 50 : 72) : (byte)125;
                image.SetPixel(x, y, new SKColor(tone, tone, tone));
                matte.GetPixelSpan()[y * matte.RowBytes + x] = x < 20 ? (byte)255 : x >= 60 ? (byte)0 : (byte)128;
            }
        var original = matte.Bytes;
        Composa.Vision.ForegroundCoverage.Refine(image, matte, null, 8, default);
        Assert.Equal(original, matte.Bytes);
    }

    [Fact]
    public void Colour_fit_opens_backdrop_gaps_between_strands_and_keeps_an_enclosed_matching_mark()
    {
        using var rgb = Pixels.NewColor(117, 77);
        var green = new SKColor(35, 151, 54);
        var light = new SKColor(233, 209, 177);
        rgb.Erase(green);
        using (var canvas = new SKCanvas(rgb))
        using (var paint = new SKPaint { Color = light })
        {
            canvas.DrawRect(37, 22, 42, 37, paint);
            canvas.DrawRect(25, 26, 15, 4, paint);
            paint.Color = new SKColor(134, 180, 116);
            canvas.DrawRect(25, 30, 12, 1, paint);
            paint.Color = green;
            canvas.DrawRect(50, 34, 7, 7, paint);
        }
        using var matte = SelectionMask.FromRect(117, 77, new(18, 14, 89, 63));
        Composa.Vision.ForegroundCoverage.Refine(rgb, matte, new(14, 9, 96, 69), 8, default);
        Assert.Equal(0, matte.GetPixel(29, 33).Alpha);
        Assert.Equal(255, matte.GetPixel(29, 28).Alpha);
        Assert.InRange(matte.GetPixel(29, 30).Alpha, 120, 137);
        Assert.Equal(255, matte.GetPixel(53, 37).Alpha);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Object_extraction_recovers_light_strands_in_copy_and_explicit_cutout(ColorDepth depth)
    {
        var session = EditorSession.NewCanvas(140, 90, SKColors.White, depth);
        var photo = Pixels.NewWorkingColor(60, 40, depth, RgbProfile.Srgb);
        var mask = Pixels.NewMask(140, 90);
        var pixels = new PixelBuffer(photo);
        var hair = new PixelValue(.89f, .84f, .71f, 1);
        var green = new PixelValue(.14f, .73f, .23f, 1);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++)
        {
            var a = x is >= 10 and < 30 ? 1f : x is >= 30 and < 34 ? .40f : 0f;
            pixels.Write(x, y, hair * a + green * (1 - a));
            mask.SetPixel(45 + x, 25 + y, new SKColor(0, 0, 0, (byte)MathF.Round(255 * a)));
        }
        var layer = session.AddImageLayer("Photo", photo, new(75, 45), fit: false);
        var original = layer.Pixels!; var bytes = original.Bytes;
        session.ApplyAiSelection(Composa.AI.AiTaskKind.ObjectSelection, mask);
        Assert.True(session.Copy());
        var clipboard = EditorSession.Clipboard!;
        var lifted = new PixelBuffer(clipboard.Pixels).Read(76 - clipboard.Origin.X, 45 - clipboard.Origin.Y);
        Assert.InRange(lifted.A, .39f, .41f);
        foreach (var bg in new[] { .05f, 1f })
        {
            var composite = lifted + new PixelValue(bg, bg, bg, 1) * (1 - lifted.A);
            Assert.InRange(Math.Abs(composite.R - (hair.R * .4f + bg * .6f)), 0, .015f);
            Assert.InRange(Math.Abs(composite.B - (hair.B * .4f + bg * .6f)), 0, .015f);
        }
        var before = session.History.CurrentId;
        using var source = MaskRefinement.Source(session.Document);
        var colors = MaskRefinement.DecontaminateColors(source, mask, 1);
        var cutout = session.AddRefinedCutout(colors, Pixels.Clone(mask), layer.Id);
        Assert.True(cutout.MaskEnabled);
        Assert.Equal(102, cutout.Mask!.GetPixel(76, 45).Alpha);
        Assert.InRange(Math.Abs(new PixelBuffer(cutout.Pixels!).Read(76, 45).R - hair.R), 0, .02f);
        Assert.False(layer.Visible);
        Assert.Same(original, layer.Pixels);
        Assert.Equal(bytes, original.Bytes);
        session.Undo(); Assert.Equal(before, session.History.CurrentId);
        Assert.Same(original, session.ActiveLayer!.Pixels); Assert.Null(session.ActiveLayer.Mask);
        // A feathered manual selection keeps the sampled RGB, even with identical coverage.
        session.Select(Pixels.Clone(mask), SelectionMode.Replace);
        Assert.True(session.Copy()); var manual = EditorSession.Clipboard!;
        var raw = new PixelBuffer(manual.Pixels).Read(76 - manual.Origin.X, 45 - manual.Origin.Y);
        Assert.True(raw.Straight.R < lifted.Straight.R - .25f);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void RGB_matting_recovers_subpixel_coverage_even_when_foreground_and_background_have_equal_luminance(ColorDepth depth)
    {
        using var image = Pixels.NewWorkingColor(80, 42, depth, RgbProfile.DisplayP3); var pixels = new PixelBuffer(image);
        using var mask = SelectionMask.FromRect(80, 42, new(0, 0, 40, 42));
        var foreground = new PixelValue(.8f, .1f, .2f, 1); var background = new PixelValue(.1f, .3f, .2803f, 1);
        for (var y = 0; y < 42; y++) for (var x = 0; x < 80; x++)
        {
            var a = Math.Clamp((46f - x) / 12, 0, 1); pixels.Write(x, y, foreground * a + background * (1 - a));
        }
        var imageBytes = image.Bytes; var maskBytes = mask.Bytes;
        using var result = AlphaMatting.Refine(image, mask, 10);
        for (var x = 30; x <= 50; x++) Assert.InRange(Math.Abs(result.GetPixel(x, 20).Alpha / 255f - Math.Clamp((46f - x) / 12, 0, 1)), 0, .045f);
        Assert.Equal(255, result.GetPixel(10, 20).Alpha); Assert.Equal(0, result.GetPixel(70, 20).Alpha);
        Assert.Equal(imageBytes, image.Bytes); Assert.Equal(maskBytes, mask.Bytes);
    }

    [Fact]
    public void Local_foreground_background_and_unknown_marks_override_the_automatic_boundary_without_touching_inputs()
    {
        using var image = Pixels.NewColor(50, 40); image.Erase(SKColors.White);
        using var mask = SelectionMask.FromRect(50, 40, new(10, 10, 40, 30)); using var edits = Pixels.NewMask(50, 40);
        edits.SetPixel(25, 20, new SKColor(0, 0, 0, (byte)MattingMark.Background));
        edits.SetPixel(2, 2, new SKColor(0, 0, 0, (byte)MattingMark.Foreground));
        edits.SetPixel(20, 20, new SKColor(0, 0, 0, (byte)MattingMark.Unknown));
        using var tri = AlphaMatting.Trimap(mask, 0, edits);
        Assert.Equal(0, tri.GetPixel(25, 20).Alpha); Assert.Equal(255, tri.GetPixel(2, 2).Alpha); Assert.Equal(128, tri.GetPixel(20, 20).Alpha);
        using var result = AlphaMatting.Refine(image, mask, 0, edits);
        Assert.Equal(0, result.GetPixel(25, 20).Alpha); Assert.Equal(255, result.GetPixel(2, 2).Alpha);
        Assert.Equal(255, mask.GetPixel(25, 20).Alpha); Assert.Equal(0, mask.GetPixel(2, 2).Alpha);
    }

    [Fact]
    public void Tiled_solver_retains_a_fine_edge_across_patch_boundaries_and_constant_regions_remain_finite()
    {
        using var image = Pixels.NewColor(1230, 44); var pixels = new PixelBuffer(image);
        using var mask = SelectionMask.FromRect(1230, 44, new(0, 0, 1230, 22));
        for (var y = 0; y < 44; y++) for (var x = 0; x < 1230; x++)
        { var a = Math.Clamp((26f - y) / 8, 0, 1); pixels.Write(x, y, new(a, .2f, 1 - a, 1)); }
        using var result = AlphaMatting.Refine(image, mask, 7);
        foreach (var x in new[] { 1, 190, 191, 192, 193, 383, 384, 1023, 1024, 1228 })
        for (var y = 17; y <= 27; y++) Assert.InRange(Math.Abs(result.GetPixel(x, y).Alpha / 255f - Math.Clamp((26f - y) / 8, 0, 1)), 0, .04f);
        using var flat = Pixels.NewColor(20, 20); flat.Erase(SKColors.Gray); using var uncertain = Pixels.NewMask(20, 20, 128);
        using var unchanged = AlphaMatting.Refine(flat, uncertain, 0);
        Assert.All(unchanged.GetPixelSpan().ToArray(), a => Assert.Equal(128, a));
        using var sharp = SelectionMask.FromRect(20, 20, new(5, 5, 15, 15)); using var preserved = AlphaMatting.Refine(flat, sharp, 4);
        Assert.Equal(sharp.Bytes, preserved.Bytes);
    }

    [Fact]
    public void Cancellation_and_grid_validation_do_not_modify_the_source()
    {
        using var image = Pixels.NewColor(40, 30); using var mask = Pixels.NewMask(40, 30); using var wrong = Pixels.NewMask(41, 30);
        Assert.Throws<ArgumentException>(() => AlphaMatting.Refine(image, wrong));
        Assert.Throws<ArgumentException>(() => AlphaMatting.Trimap(mask, 8, wrong));
        Assert.ThrowsAny<OperationCanceledException>(() => AlphaMatting.Refine(image, mask, cancellation: new CancellationToken(true)));
        Assert.True(SelectionMask.IsEmpty(mask));
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Decontamination_unmixes_the_old_background_preserves_texture_and_has_a_controllable_amount(ColorDepth depth)
    {
        using var image = Pixels.NewWorkingColor(80, 30, depth, RgbProfile.DisplayP3); using var mask = Pixels.NewMask(80, 30);
        var input = new PixelBuffer(image);
        for (var y = 0; y < 30; y++) for (var x = 0; x < 80; x++)
        {
            var a = Math.Clamp((50f - x) / 18, 0, 1); var foreground = new PixelValue(.16f + y * .002f, .23f, .35f, 1); var background = new PixelValue(.9f, .8f, .6f, 1);
            input.Write(x, y, foreground * a + background * (1 - a)); mask.SetPixel(x, y, new SKColor(0, 0, 0, (byte)MathF.Round(a * 255)));
        }
        var original = image.Bytes;
        using var noChange = MaskRefinement.DecontaminateColors(image, mask, 0); Assert.Equal(original, noChange.Bytes);
        using var cleaned = MaskRefinement.DecontaminateColors(image, mask, 1); using var partial = MaskRefinement.DecontaminateColors(image, mask, .5);
        Assert.Equal(Pixels.ProfileOf(image), Pixels.ProfileOf(cleaned)); Assert.Equal(image.ColorType, cleaned.ColorType);
        var actual = new PixelBuffer(cleaned); var half = new PixelBuffer(partial);
        foreach (var x in new[] { 35, 39, 43 }) foreach (var y in new[] { 5, 15, 25 })
        {
            Assert.InRange(Math.Abs(actual.Read(x, y).R - (.16f + y * .002f)), 0, .025f);
            Assert.InRange(Math.Abs(actual.Read(x, y).B - .35f), 0, .025f);
            Assert.InRange(Math.Abs(half.Read(x, y).R - (actual.Read(x, y).R + input.Read(x, y).R) / 2), 0, .005f);
            Assert.Equal(input.Read(x, y).A, actual.Read(x, y).A);
        }
        Assert.Equal(input.Read(10, 10), actual.Read(10, 10)); Assert.Equal(original, image.Bytes);
        using var png = Pixels.NewLike(image); var transparent = new PixelBuffer(png);
        for (var y = 0; y < 30; y++) for (var x = 0; x < 80; x++) transparent.Write(x, y, new(.1f, .2f, .3f, .4f));
        using var untouched = MaskRefinement.DecontaminateColors(png, mask, 1); Assert.Equal(png.Bytes, untouched.Bytes);
    }

    [Fact]
    public void Locked_mask_conversion_keeps_the_selection_and_history()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.Blue);
        var layer = session.ActiveLayer!;
        session.SelectRect(new(15, 10, 45, 30));
        session.SetLayerLocks(layer, LayerLocks.Pixels);
        var state = session.History.CurrentId;
        var selection = session.Selection!;

        session.AddMask(layer);
        Assert.False(session.RefinedSelectionToMask(selection));
        Assert.Same(selection, session.Selection);
        Assert.Null(layer.Mask);
        Assert.Equal(state, session.History.CurrentId);
    }

    [Fact]
    public void Refined_output_keeps_an_editable_mask_hides_only_its_source_and_undo_restores_the_old_mask_properties()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.Blue, ColorDepth.Sixteen); var source = session.ActiveLayer!; var pixels = source.Pixels!;
        source.Mask = Pixels.NewMask(10, 12, 255); source.MaskLinked = false; source.MaskTransform = LayerTransform.Identity(10, 12) with { X = 20 }; source.MaskDensity = .6; source.MaskFeather = 3; source.MaskDefault = 255;
        using var selected = SelectionMask.FromRect(60, 40, new(15, 10, 45, 30)); var oldMask = source.Mask; var oldPlacement = source.MaskTransform;
        session.RefinedSelectionToMask(selected); Assert.True(source.MaskLinked); Assert.Null(source.MaskTransform); Assert.Equal(1, source.MaskDensity); Assert.Equal(0, source.MaskFeather); Assert.Equal(0, source.MaskDefault);
        session.Undo(); source = session.ActiveLayer!; Assert.Same(oldMask, source.Mask); Assert.Equal(oldPlacement, source.MaskTransform); Assert.False(source.MaskLinked); Assert.Equal(.6, source.MaskDensity);
        var colors = Pixels.Clone(pixels); var mask = Pixels.Clone(selected); var refined = session.AddRefinedCutout(colors, mask, source.Id);
        Assert.Same(colors, refined.Pixels); Assert.Same(mask, refined.Mask); Assert.False(source.Visible); Assert.True(refined.Visible); Assert.Equal(2, session.Document.Layers.Count);
        session.Undo(); Assert.Single(session.Document.Layers); Assert.True(session.ActiveLayer!.Visible); Assert.Same(pixels, session.ActiveLayer.Pixels);
        session.Redo(); Assert.Same(colors, session.ActiveLayer!.Pixels); Assert.Same(mask, session.ActiveLayer.Mask); Assert.Equal(255, mask.GetPixel(30, 20).Alpha);
    }
}

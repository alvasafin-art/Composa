using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionRetouchTests
{
    private static SKBitmap Texture(bool blemish)
    {
        var result = Pixels.NewColor(128, 96, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(result);
        var alpha = 32767 / 65535f;
        for (var y = 0; y < 96; y++)
        for (var x = 0; x < 128; x++)
        {
            var value = (30000 + (x % 16) * 23 + (y % 16) * 11) / 65535f;
            if (blemish && x is >= 60 and < 68 && y is >= 44 and < 52) value = .05f;
            pixels.Write(x, y, new PixelValue(value, value * .8f, value * .6f, alpha).Premultiplied);
        }
        return result;
    }

    [Fact]
    public void Healing_transfers_low_bit_texture_without_mutating_the_source_or_its_fine_alpha()
    {
        using var dirty = Texture(true); using var clean = Texture(false);
        using var mask = SelectionMask.FromRect(128, 96, new(58, 42, 70, 54));
        var bytes = dirty.Bytes;
        using var repaired = PatchBlend.Blend(dirty, dirty, mask, new(32, 0), preserveAlpha: true);
        Assert.Equal(SKColorType.RgbaF32, repaired.ColorType);
        var actual = new PixelBuffer(repaired); var expected = new PixelBuffer(clean);
        for (var y = 44; y < 52; y++)
        for (var x = 60; x < 68; x++)
        {
            Assert.InRange(Math.Abs(actual.Read(x, y).R - expected.Read(x, y).R), 0, 1e-6);
            Assert.Equal(expected.Read(x, y).A, actual.Read(x, y).A);
        }
        Assert.Equal(bytes, dirty.Bytes);
    }

    [Fact]
    public void Inpaint_exemplar_and_refinement_write_float_samples_and_preserve_unselected_pixels()
    {
        using var dirty = Texture(true);
        using var mask = SelectionMask.FromRect(128, 96, new(58, 42, 70, 54));
        var before = dirty.Bytes;
        using var native = Inpaint.Fill(dirty, mask, coherentSpot: true);
        using var exemplar = ExemplarFill.TryFill(dirty, mask, new(58, 42, 70, 54), null)!;
        Assert.NotNull(exemplar);
        using var refined = PatchMatchRefinement.Fill(dirty, exemplar, mask, null, default);
        foreach (var result in new[] { native, exemplar, refined })
        {
            Assert.Equal(SKColorType.RgbaF32, result.ColorType);
            var colors = new PixelBuffer(result);
            Assert.Equal(new PixelBuffer(dirty).Read(5, 5), colors.Read(5, 5));
            var center = colors.Read(64, 48);
            Assert.True(center.R > .15);
            Assert.InRange(Math.Abs(center.A - 32767 / 65535f), 0, 1e-6);
            // The repaired texture contains information below a display byte's resolution.
            var levels = Enumerable.Range(60, 8).Select(x => colors.Read(x, 48).Straight.R).ToArray();
            Assert.Contains(levels, value => Math.Abs(value * 255 - MathF.Round(value * 255)) > .01);
        }
        Assert.Equal(before, dirty.Bytes);
    }

    [Fact]
    public void A_real_session_healing_stroke_commits_and_undoes_float_pixels()
    {
        using var dirty = Texture(true);
        var document = new Document(128, 96) { Depth = ColorDepth.Sixteen };
        document.InsertAboveActive(Layer.Raster("Texture", dirty));
        var session = new EditorSession(document) { Tool = Tool.SpotHealing, Brush = new() { Size = 18, Hardness = 1, PressureSize = false } };
        Assert.True(session.BeginStroke(new(64, 48), out _)); session.EndStroke();
        Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer!.Pixels!.ColorType);
        Assert.True(new PixelBuffer(session.ActiveLayer.Pixels).Read(64, 48).R > .15);
        session.Undo(); Assert.Same(dirty, session.ActiveLayer!.Pixels);
        session.Redo(); Assert.Equal(SKColorType.RgbaF32, session.ActiveLayer!.Pixels!.ColorType);
    }
}

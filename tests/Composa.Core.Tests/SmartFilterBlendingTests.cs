using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SmartFilterBlendingTests
{
    public static TheoryData<BlendMode> NativeModes => new(Enum.GetValues<BlendMode>().Where(m => !m.IsCustom() && m != BlendMode.PassThrough));

    [Theory]
    [MemberData(nameof(NativeModes))]
    public void Opaque_filter_color_blending_agrees_with_the_independent_Skia_blender(BlendMode mode)
    {
        using var background = Pixels.NewColor(32, 24, ColorDepth.Sixteen); using var source = Pixels.NewLike(background);
        var b = new PixelBuffer(background); var s = new PixelBuffer(source);
        for (var y = 0; y < 24; y++) for (var x = 0; x < 32; x++)
        {
            b.Write(x, y, new(x / 31f, y / 23f, .35f, 1)); s.Write(x, y, new(.65f, 1 - x / 31f, 1 - y / 23f, 1));
        }
        using var expected = Pixels.Clone(background);
        using (var canvas = new SKCanvas(expected))
        using (var paint = new SKPaint { BlendMode = mode.ToSkia() }) canvas.DrawBitmap(source, 0, 0, paint);
        FilterBlending.Apply(background, source, 0, 0, 1, mode);
        var a = new PixelBuffer(source); var e = new PixelBuffer(expected);
        double worst = 0;
        for (var y = 0; y < 24; y++) for (var x = 0; x < 32; x++)
            for (var c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(a.Read(x, y)[c] - e.Read(x, y)[c]));
        // Skia differs slightly around Dodge/Burn divisions; exact division is checked separately below.
        Assert.InRange(worst, 0, mode is BlendMode.ColorDodge or BlendMode.ColorBurn ? .0005 : .00001);
    }

    [Fact]
    public void Dodge_and_burn_keep_full_precision_at_fractional_and_singular_inputs()
    {
        var b = new PixelValue(.2f, .9f, 0, 1); var s = new PixelValue(.4f, .4f, 1, 1);
        var dodge = FilterBlending.Color(b, s, BlendMode.ColorDodge); var burn = FilterBlending.Color(b, s, BlendMode.ColorBurn);
        Assert.InRange(Math.Abs(dodge.R - 1f / 3), 0, .000001); Assert.Equal(1, dodge.G); Assert.Equal(0, dodge.B);
        Assert.Equal(0, burn.R); Assert.InRange(Math.Abs(burn.G - .75f), 0, .000001); Assert.Equal(0, burn.B);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Opacity_zero_restores_the_input_and_half_opacity_does_not_leave_a_sharp_opaque_edge(ColorDepth depth)
    {
        var session = EditorSession.NewCanvas(80, 60, SKColors.Transparent, depth: depth); var layer = session.ActiveLayer!;
        session.SelectRect(new(20, 10, 60, 50)); session.Fill(SKColors.Red); session.Deselect(); var source = layer.Pixels!;
        var originalBytes = source.Bytes;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 3 }); var filter = layer.SmartFilters.Single();
        var x = 20 + layer.FilterPaddingX; var y = 30 + layer.FilterPaddingY;
        var blurredAlpha = new PixelBuffer(layer.Pixels!).Read(x, y).A;
        session.ChangeSmartFilter(layer, filter.Id, opacity: .5); var halfway = new PixelBuffer(layer.Pixels!).Read(x, y).A;
        Assert.InRange(Math.Abs(halfway - (.5f + blurredAlpha / 2)), 0, depth == ColorDepth.Sixteen ? .00001 : .005);
        session.ChangeSmartFilter(layer, filter.Id, opacity: 0); using var flat = session.Flatten();
        Assert.Equal(source.Bytes, flat.Bytes); Assert.Equal(originalBytes, source.Bytes);
    }

    [Fact]
    public void Filter_order_and_blending_round_trip_and_undo_without_mutating_committed_results()
    {
        var session = EditorSession.NewCanvas(40, 30, new SKColor(100, 130, 160)); var layer = session.ActiveLayer!;
        session.AddSmartFilter(layer, new() { Kind = FilterKind.AddNoise, Amount = 40, Seed = 19 }); var noise = layer.SmartFilters.Single();
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2, ClampEdges = true });
        var smooth = layer.Pixels!; var bytes = smooth.Bytes;
        session.MoveSmartFilter(layer, noise.Id, 1); Assert.Equal(noise.Id, layer.SmartFilters[^1].Id); Assert.NotEqual(bytes, layer.Pixels!.Bytes);
        Assert.Equal(bytes, smooth.Bytes); session.Undo(); layer = session.ActiveLayer!; Assert.Same(smooth, layer.Pixels);
        session.ChangeSmartFilter(layer, noise.Id, opacity: .35, blend: BlendMode.SoftLight);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var restored = ProjectFile.Read(stream); var actual = restored.ActiveLayer!;
        Assert.Equal(.35, actual.SmartFilters[0].Opacity); Assert.Equal(BlendMode.SoftLight, actual.SmartFilters[0].Blend);
        var oldBytes = actual.Pixels!.Bytes; var editor = new EditorSession(restored);
        editor.ChangeSmartFilter(actual, noise.Id, opacity: .35, blend: BlendMode.SoftLight); Assert.Equal(oldBytes, actual.Pixels!.Bytes);
    }

    [Fact]
    public void Every_filter_keeps_the_working_profile_for_RGB8_as_well_as_RGB16()
    {
        foreach (var depth in new[] { ColorDepth.Eight, ColorDepth.Sixteen })
        foreach (var kind in Enum.GetValues<FilterKind>())
        {
            using var source = Pixels.NewWorkingColor(24, 20, depth, RgbProfile.DisplayP3); source.Erase(SKColors.Green);
            var settings = new FilterSettings { Kind = kind, Radius = 1, Amount = 0, BloomAmount = 0, Distortion = 0,
                VignetteAmount = 0, CameraRaw = new(), Painterly = new() { BrushSize = 4 } };
            var result = ImageFilters.Run(source, settings);
            using (result.Result)
            {
                Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(result.Result));
                if (kind is FilterKind.GaussianBlur or FilterKind.MotionBlur or FilterKind.Sharpen or FilterKind.LensCorrection)
                {
                    var expected = source.GetPixel(12, 10); var actual = result.Result.GetPixel(12 + result.GrowX, 10 + result.GrowY);
                    Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 1); Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 1); Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 1);
                }
            }
        }
    }
}

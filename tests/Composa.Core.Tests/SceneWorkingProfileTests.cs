using Composa.AI;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SceneWorkingProfileTests
{
    [Fact]
    public void Whole_image_relight_keeps_exact_high_precision_source_alpha()
    {
        var session = EditorSession.NewCanvas(32, 24, null, ColorDepth.Sixteen);
        session.AssignColorProfile(RgbProfile.Rgb1998);
        var pixels = new PixelBuffer(session.ActiveLayer!.Pixels!);
        for (var y = 0; y < 24; y++) for (var x = 0; x < 32; x++) pixels.Write(x, y, new PixelValue(.4f, .6f, .2f, 30001 / 65535f).Premultiplied);
        var request = new AiTaskRequest { Task = AiTaskKind.Relight, Scene = new() { Scope = RelightScope.WholeImage, AddShadows = false } };
        using var input = SceneEditInput.Prepare(session.Document, request);
        var result = SceneEditResult.Extract(input, input.Before, request);
        try { Assert.Equal(30001 / 65535f, new PixelBuffer(Assert.Single(result.Layers).Pixels).Read(10, 10).A); }
        finally { foreach (var layer in result.Layers) layer.Pixels.Dispose(); }
    }

    [Fact]
    public void Linear_basis_change_keeps_extended_gamut_and_matches_the_CMM_inside_gamut()
    {
        var toModel = new LinearRgbTransform(RgbProfile.DisplayP3, RgbProfile.Srgb);
        var fromModel = new LinearRgbTransform(RgbProfile.Srgb, RgbProfile.DisplayP3);
        var green = toModel.Map(0, 1, 0);
        Assert.True(green.R < 0 && green.G > 1);
        var roundtrip = fromModel.Map(green.R, green.G, green.B);
        Assert.InRange(Math.Abs(roundtrip.R), 0, 1e-12); Assert.InRange(Math.Abs(roundtrip.G - 1), 0, 1e-12); Assert.InRange(Math.Abs(roundtrip.B), 0, 1e-12);
        using var image = Pixels.NewWorkingColor(1, 1, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        new PixelBuffer(image).Write(0, 0, new(.4f, .6f, .2f, 1));
        using var reference = Pixels.ConvertWorking(image, ColorDepth.Sixteen, RgbProfile.Srgb);
        var expected = new PixelBuffer(reference).Read(0, 0);
        var transfer = RgbProfile.Rgb1998.Transfer;
        var linear = new LinearRgbTransform(RgbProfile.Rgb1998, RgbProfile.Srgb).Map(transfer.Decode(.4), transfer.Decode(.6), transfer.Decode(.2));
        Assert.InRange(Math.Abs(RgbProfile.Srgb.Transfer.Encode(linear.R) - expected.R), 0, .0003);
        Assert.InRange(Math.Abs(RgbProfile.Srgb.Transfer.Encode(linear.G) - expected.G), 0, .0003);
        Assert.InRange(Math.Abs(RgbProfile.Srgb.Transfer.Encode(linear.B) - expected.B), 0, .0003);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Neutral_scene_model_keeps_wide_gamut_subject_numbers_profile_detail_and_alpha(ColorDepth depth)
    {
        var doc = new Document(640, 64) { Depth = depth, Profile = RgbProfile.DisplayP3 }; var session = new EditorSession(doc);
        session.AddBlankLayer(); session.Fill(SKColors.LightGray);
        var image = Pixels.NewWorkingColor(512, 24, depth, doc.Profile); var pixels = new PixelBuffer(image);
        for (var y = 0; y < 24; y++) for (var x = 0; x < 512; x++)
            pixels.Write(x, y, new PixelValue((24000 + x) / 65535f, 1, 0, y == 23 ? 30001 / 65535f : 1).Premultiplied);
        session.AddImageLayer("Wide PNG", image, new(320, 30), fit: false); var bytes = image.Bytes;
        var request = new AiTaskRequest { Task = AiTaskKind.Harmonize, Scene = new() { PreserveDetails = true, AddShadows = false, Strength = 1 } };
        using var input = SceneEditInput.Prepare(doc, request);
        Assert.True(Pixels.IsSrgb8(input.Before)); Assert.True(Pixels.IsSrgb8(input.Background)); Assert.True(Pixels.IsSrgb8(input.ModelForeground));
        Assert.Equal(doc.Profile, Pixels.ProfileOf(input.Foreground)); Assert.Equal(depth, Pixels.DepthOf(input.Foreground));
        var result = SceneEditResult.Extract(input, input.Before, request);
        try
        {
            var actual = Assert.Single(result.Layers).Pixels;
            Assert.Equal(doc.Profile, Pixels.ProfileOf(actual)); Assert.Equal(depth, Pixels.DepthOf(actual));
            var a = new PixelBuffer(actual); var e = new PixelBuffer(input.Foreground);
            for (var y = 0; y < 64; y++) for (var x = 0; x < 640; x++) for (var c = 0; c < 4; c++)
                Assert.InRange(Math.Abs(a.Read(x, y)[c] - e.Read(x, y)[c]), 0, .0000002);
            Assert.Equal(bytes, image.Bytes);
            if (depth == ColorDepth.Sixteen) Assert.Equal(512, Enumerable.Range(64, 512).Select(x => a.Read(x, 20).R).Distinct().Count());
        }
        finally { foreach (var layer in result.Layers) { layer.Pixels.Dispose(); layer.Mask?.Dispose(); } }
    }

    [Fact]
    public void Masked_model_edit_converts_into_working_RGB_and_keeps_unedited_float_pixels_exact()
    {
        using var original = Pixels.NewWorkingColor(512, 2, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        var source = new PixelBuffer(original);
        for (var y = 0; y < 2; y++) for (var x = 0; x < 512; x++) source.Write(x, y, new((24000+x)/65535f, .6f, .2f, 1));
        var bytes = original.Bytes;
        using var generated = Pixels.NewColor(512, 2); generated.Erase(SKColors.CornflowerBlue);
        using var mask = Pixels.NewMask(512, 2); mask.GetPixelSpan()[256] = 255;
        using var result = AiResultPostprocessor.Constrain(generated, original, mask);
        using var expected = Pixels.ConvertWorking(generated, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        Assert.Equal(RgbProfile.Rgb1998, Pixels.ProfileOf(result)); var output = new PixelBuffer(result);
        Assert.Equal(new PixelBuffer(expected).Read(256, 0), output.Read(256, 0));
        for (var x = 0; x < 512; x++) Assert.Equal(source.Read(x, 1), output.Read(x, 1));
        Assert.Equal(bytes, original.Bytes);
    }
}

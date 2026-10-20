using Composa.App.AI;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.ColorManagement;
using SkiaSharp;

namespace Composa.App.Tests;

public class HighPrecisionAiInteropTests
{
    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Model_upload_converts_wide_gamut_RGB8_and_RGB16_to_sRGB_before_encoding(ColorDepth depth)
    {
        using var source = Pixels.NewWorkingColor(16, 12, depth, RgbProfile.DisplayP3);
        if (depth == ColorDepth.Sixteen)
        {
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
            for (var i = 0; i < 16 * 12; i++) { pixels[i * 4] = 100 / 255f; pixels[i * 4 + 1] = 150 / 255f; pixels[i * 4 + 2] = 200 / 255f; pixels[i * 4 + 3] = 1; }
        }
        else
        {
            var pixels = source.GetPixelSpan();
            for (var i = 0; i < 16 * 12; i++) { pixels[i * 4] = 100; pixels[i * 4 + 1] = 150; pixels[i * 4 + 2] = 200; pixels[i * 4 + 3] = 255; }
        }
        var original = source.Bytes; var png = ComfyClient.EncodeUploadPng(source);
        using var reference = new ImageMagick.MagickImage(new ImageMagick.MagickColor(100 * 257, 150 * 257, 200 * 257), 16, 12);
        reference.SetProfile(new ImageMagick.ColorProfile(RgbProfile.DisplayP3.IccBytes()));
        reference.RenderingIntent = ImageMagick.RenderingIntent.Relative;
        reference.TransformColorSpace(new ImageMagick.ColorProfile(RgbProfile.Srgb.IccBytes()), ImageMagick.ColorTransformMode.HighRes);
        using var referencePixels = reference.GetPixels(); var expected = referencePixels.GetPixel(4, 4).ToArray();
        using var stream = new MemoryStream(png); using var decoded = Composa.IO.ImageFiles.Load(stream, preserveProfile: true);
        Assert.True(Pixels.IsSrgb8(decoded)); var color = decoded.GetPixel(4, 4);
        Assert.InRange(Math.Abs(color.Red - expected[0] / 257d), 0, 2); Assert.InRange(Math.Abs(color.Green - expected[1] / 257d), 0, 2); Assert.InRange(Math.Abs(color.Blue - expected[2] / 257d), 0, 2);
        Assert.Equal(original, source.Bytes);
    }

    [Fact]
    public void Paid_scene_editing_uses_RGB8_copies_for_stitching_and_keeps_the_precise_foreground()
    {
        var session = EditorSession.NewCanvas(96, 64, SKColors.LightGray, ColorDepth.Sixteen);
        var source = Pixels.NewColor(32, 32, ColorDepth.Sixteen); source.Erase(SKColors.Red);
        session.AddImageLayer("Product", source, new(48, 30), fit: false);
        var bytes = source.Bytes;
        var request = new AiTaskRequest { Task = AiTaskKind.Harmonize };
        using var scene = SceneEditInput.Prepare(session.Document.Clone(), request);
        using var inputs = AiTaskService.SceneApiInputs(scene, request);
        Assert.Equal(SKColorType.Rgba8888, inputs.SourceImage.ColorType);
        Assert.Equal(SKColorType.Rgba8888, inputs.ContextImage.ColorType);
        using var partner = new PartnerImageInputs(inputs, request with { Task = AiTaskKind.ImageEdit });
        var generated = Pixels.NewColor(96, 64); generated.Erase(SKColors.Blue);
        using var finished = partner.Finish(generated);
        Assert.Equal(SKColorType.Rgba8888, finished.ColorType); Assert.Equal(SKColors.Blue, finished.GetPixel(48, 30));
        Assert.Equal(SKColorType.RgbaF32, scene.Foreground.ColorType); Assert.Equal(bytes, source.Bytes);
    }

    [Fact]
    public void Model_upload_is_RGB8_without_replacing_the_high_precision_source()
    {
        using var source = Pixels.NewColor(16, 12, ColorDepth.Sixteen);
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 24001 / 65535f; pixels[i + 1] = .5f; pixels[i + 2] = .7f; pixels[i + 3] = 1; }
        var original = source.Bytes;
        var png = ComfyClient.EncodeUploadPng(source);
        Assert.Equal(8, png[24]);
        using var decoded = SKBitmap.Decode(png);
        Assert.InRange(decoded.GetPixel(4, 4).Red, 92, 94);
        Assert.Equal(original, source.Bytes); Assert.Equal(SKColorType.RgbaF32, source.ColorType);
    }
}

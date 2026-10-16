using Composa.App.AI;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class HighPrecisionAiInteropTests
{
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

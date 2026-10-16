using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionVisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refinement_and_decontamination_preserve_source_precision(bool decontaminate)
    {
        using var image = Pixels.NewColor(96, 64, ColorDepth.Sixteen);
        var source = new PixelBuffer(image);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 96; x++) source.Write(x, y, new((24000 + x) / 65535f, .5f, .7f, 1));
        using var mask = SelectionMask.FromRect(96, 64, new(16, 12, 80, 56));
        var original = image.Bytes;
        using var refined = MaskRefinement.Refine(mask, image, new(Feather: 1));
        Assert.Equal(255, refined.GetPixel(48, 32).Alpha); Assert.Equal(0, refined.GetPixel(0, 0).Alpha);
        using var cutout = MaskRefinement.Cutout(image, refined, decontaminate);
        Assert.Equal(SKColorType.RgbaF32, cutout.ColorType);
        var result = new PixelBuffer(cutout);
        for (var x = 30; x < 64; x++) Assert.Equal(source.Read(x, 32), result.Read(x, 32));
        Assert.Equal(34, Enumerable.Range(30, 34).Select(x => result.Read(x, 32).R).Distinct().Count());
        Assert.Equal(original, image.Bytes);
    }

    [Fact]
    public void Guided_filter_and_plain_object_selection_read_float_alpha_and_edges()
    {
        using var image = Pixels.NewColor(64, 48, ColorDepth.Sixteen);
        image.Erase(SKColors.White);
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = SKColors.Red }) canvas.DrawRect(16, 8, 32, 32, paint);
        var original = image.Bytes;
        using var selected = ObjectSelection.Select(image, 32, 24, 0, smooth: false);
        Assert.NotNull(selected); Assert.Equal(255, selected.GetPixel(32, 24).Alpha); Assert.Equal(0, selected.GetPixel(0, 0).Alpha);
        var coarse = Enumerable.Repeat(1f, 8 * 6).ToArray();
        var pixels = new PixelBuffer(image); pixels.Write(0, 0, default); Pixels.Invalidate(image);
        using var guided = GuidedFilter.Upsample(coarse, 8, 6, image);
        Assert.Equal(0, guided.GetPixel(0, 0).Alpha); Assert.Equal(255, guided.GetPixel(32, 24).Alpha);
        Assert.Equal(original.Skip(16), image.Bytes.Skip(16));
    }

    private static EditorSession Scene()
    {
        var session = EditorSession.NewCanvas(96, 64, SKColors.LightGray, ColorDepth.Sixteen);
        var objectImage = Pixels.NewColor(32, 32, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(objectImage);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            var a = y == 31 ? 30001 / 65535f : 1;
            pixels.Write(x, y, new PixelValue((24000 + x) / 65535f, .4f, .6f, a).Premultiplied);
        }
        session.AddImageLayer("PNG", objectImage, new(48, 30), fit: false);
        return session;
    }

    [Theory]
    [InlineData(AiTaskKind.ChangeBackground)]
    [InlineData(AiTaskKind.Harmonize)]
    public void Scene_outputs_preserve_original_subject_low_bits_and_exact_alpha(AiTaskKind task)
    {
        var session = Scene(); var original = session.ActiveLayer!.Pixels!; var bytes = original.Bytes;
        var request = new AiTaskRequest { Task = task, Scene = new() { Strength = 0, AddShadows = false } };
        using var input = SceneEditInput.Prepare(session.Document.Clone(), request);
        Assert.True(input.IsCutout);
        using var generated = Pixels.DisplayCopy(input.Before);
        var output = SceneEditResult.Extract(input, generated, request);
        try
        {
            var layer = Assert.Single(output.Layers, l => l.Tags!.Contains("scene-object"));
            Assert.Equal(SKColorType.RgbaF32, layer.Pixels.ColorType);
            var a = new PixelBuffer(layer.Pixels); var e = new PixelBuffer(input.Foreground);
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 96; x++)
            for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(a.Read(x, y)[c] - e.Read(x, y)[c]), 0, .0000001);
            Assert.Equal(bytes, original.Bytes);
        }
        finally { foreach (var layer in output.Layers) { layer.Pixels.Dispose(); layer.Mask?.Dispose(); } }
    }

    [Fact]
    public void General_AI_inputs_are_separate_RGB8_model_copies()
    {
        var session = Scene(); var original = session.ActiveLayer!.Pixels!; var bytes = original.Bytes;
        using var inputs = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.ImageEdit, ReferenceImage = original, ReferenceMegapixels = null });
        foreach (var image in inputs.Images().Values.Where(i => i.ColorType != SKColorType.Alpha8)) Assert.Equal(SKColorType.Rgba8888, image.ColorType);
        Assert.Equal(SKColorType.RgbaF32, original.ColorType); Assert.Equal(bytes, original.Bytes);
        using var modelResult = Pixels.NewColor(original.Width, original.Height); modelResult.Erase(SKColors.Blue);
        using var alphaRestored = Pixels.WithAlphaOf(modelResult, original);
        Assert.Equal(SKColorType.RgbaF32, alphaRestored.ColorType);
        Assert.Equal(new PixelBuffer(original).Read(0, 31).A, new PixelBuffer(alphaRestored).Read(0, 31).A);
    }
}

using Composa.Rendering;
using Composa.Editing;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PromptSegmentationTests
{
    [Theory]
    [InlineData(PromptModelKind.MobileSam, -1)]
    [InlineData(PromptModelKind.MobileSam, 0)]
    [InlineData(PromptModelKind.MobileSam, 1)]
    [InlineData(PromptModelKind.EfficientSamTi, -1)]
    [InlineData(PromptModelKind.EfficientSamTi, 0)]
    [InlineData(PromptModelKind.EfficientSamTi, 1)]
    public async Task Committed_prompt_mask_survives_inference_cleanup_render_and_history(PromptModelKind kind, int edge)
    {
        var model = kind == PromptModelKind.MobileSam ? PromptModels.MobileSam : PromptModels.EfficientSamTi;
        var session = EditorSession.NewCanvas(160, 96, SKColors.White); session.ObjectEdgeOffset = edge;
        var image = Pixels.NewColor(160, 96); image.Erase(SKColors.White);
        using (var canvas = new SKCanvas(image)) using (var paint = new SKPaint { Color = SKColors.Red }) canvas.DrawRect(45, 20, 65, 50, paint);
        session.AddImageLayer("Object", image, new SKPoint(80, 48), fit: false);
        await session.SelectPromptObjectAsync(model, null, new SKRectI(40, 15, 115, 75), SelectionMode.Replace);
        var selection = Assert.IsType<SKBitmap>(session.Selection);
        Assert.Equal(160, selection.Width); Assert.Equal(96, selection.Height);
        Assert.NotEqual(IntPtr.Zero, selection.GetPixels());
        var before = selection.GetPixelSpan().ToArray();
        using var render = session.Flatten();
        session.Undo(); Assert.Null(session.Selection); session.Redo();
        Assert.Equal(before, session.Selection!.GetPixelSpan().ToArray());
        await session.SelectPromptObjectAsync(model, new SKPointI(70, 40), null, SelectionMode.Add);
        Assert.Equal(160, session.Selection!.Width);
    }

    [Theory]
    [InlineData(PromptModelKind.MobileSam)]
    [InlineData(PromptModelKind.EfficientSamTi)]
    public void Real_encoder_decoder_accept_point_and_box_on_non_square_image(PromptModelKind kind)
    {
        var model = kind == PromptModelKind.MobileSam ? PromptModels.MobileSam : PromptModels.EfficientSamTi;
        Assert.True(model.Encoder.Verify()); Assert.True(model.Decoder.Verify());
        using var image = Pixels.NewColor(160, 96); image.Erase(SKColors.White);
        using (var canvas = new SKCanvas(image)) { using var paint = new SKPaint { Color = SKColors.Red }; canvas.DrawRect(45, 20, 65, 50, paint); }
        var embedding = PromptSegmentation.Encode(image, model);
        using var point = PromptSegmentation.Decode(embedding, model, image.Width, image.Height, new SKPoint(70, 40), null);
        using var box = PromptSegmentation.Decode(embedding, model, image.Width, image.Height, null, new SKRectI(40, 15, 115, 75));
        Assert.Equal(image.Width, point.Width); Assert.Equal(image.Height, point.Height);
        Assert.True(box.GetPixel(70, 40).Alpha > box.GetPixel(10, 10).Alpha, "Box mask should prefer the prompted object to the background.");
    }
}

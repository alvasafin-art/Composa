using Composa.Rendering;
using Composa.Editing;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PromptSegmentationTests
{
    private static PromptModel Model(PromptModelKind kind) => kind switch
    { PromptModelKind.MobileSam => PromptModels.MobileSam, PromptModelKind.EfficientSamTi => PromptModels.EfficientSamTi, _ => PromptModels.EfficientSamS };

    [Theory]
    [InlineData(PromptModelKind.MobileSam, 240, 96)]
    [InlineData(PromptModelKind.MobileSam, 96, 240)]
    [InlineData(PromptModelKind.EfficientSamTi, 96, 240)]
    [InlineData(PromptModelKind.EfficientSamS, 240, 96)]
    [InlineData(PromptModelKind.EfficientSamS, 96, 240)]
    public void Real_models_preserve_object_geometry_on_landscape_and_portrait(PromptModelKind kind, int width, int height)
    {
        using var image = Pixels.NewColor(width, height); image.Erase(SKColors.White);
        var objectBounds = new SKRectI(width / 4, height / 4, width * 3 / 4, height * 3 / 4);
        using (var canvas = new SKCanvas(image)) using (var paint = new SKPaint { Color = new SKColor(190, 35, 45) }) canvas.DrawRect(objectBounds, paint);
        var model = Model(kind); Assert.True(model.Encoder.Verify()); Assert.True(model.Decoder.Verify());
        var embedding = PromptSegmentation.Encode(image, model);
        using var mask = PromptSegmentation.Decode(embedding, model, width, height, null,
            new SKRectI(objectBounds.Left - 3, objectBounds.Top - 3, objectBounds.Right + 3, objectBounds.Bottom + 3));
        int intersection = 0, union = 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var actual = mask.GetPixel(x, y).Alpha >= 128; var expected = objectBounds.Contains(x, y);
            if (actual && expected) intersection++; if (actual || expected) union++;
        }
        var iou = (double)intersection / Math.Max(1, union);
        Assert.True(iou > .88, $"{kind}, {width}x{height}: IoU {iou:0.000}");
        Assert.Equal((byte)0, mask.GetPixel(3, 3).Alpha);
        Assert.Equal((byte)255, mask.GetPixel(width / 2, height / 2).Alpha);
    }

    [Fact]
    public void Mobile_logit_reconstruction_removes_padded_rows_before_resizing_portrait()
    {
        var logits = Enumerable.Repeat(-8f, 256 * 256).ToArray();
        for (var y = 64; y < 192; y++) for (var x = 24; x < 72; x++) logits[y * 256 + x] = 8;
        using var mask = PromptSegmentation.Reconstruct(logits, 256, 256, 384, 1024, 96, 256, true);
        Assert.Equal(new SKRectI(24, 64, 72, 192), SelectionMask.Bounds(mask, 128));
        Assert.Equal((byte)0, mask.GetPixel(90, 230).Alpha);
    }
    [Theory]
    [InlineData(PromptModelKind.MobileSam, -1)]
    [InlineData(PromptModelKind.MobileSam, 0)]
    [InlineData(PromptModelKind.MobileSam, 1)]
    [InlineData(PromptModelKind.EfficientSamTi, -1)]
    [InlineData(PromptModelKind.EfficientSamTi, 0)]
    [InlineData(PromptModelKind.EfficientSamTi, 1)]
    public async Task Committed_prompt_mask_survives_inference_cleanup_render_and_history(PromptModelKind kind, int edge)
    {
        var model = Model(kind);
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
    [InlineData(PromptModelKind.EfficientSamS)]
    public void Real_encoder_decoder_accept_point_and_box_on_non_square_image(PromptModelKind kind)
    {
        var model = Model(kind);
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

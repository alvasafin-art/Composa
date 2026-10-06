using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PromptSegmentationTests
{
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

using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

[Collection(ClipboardCollection.Name)]
public class ClipboardProfileTests
{
    [Fact]
    public void Copied_live_layers_keep_the_profile_at_copy_time_even_if_source_changes_before_paste()
    {
        var source = EditorSession.NewCanvas(128, 96, null, ColorDepth.Sixteen);
        source.AssignColorProfile(RgbProfile.Rgb1998);
        var shape = source.AddShape(new ShapeStyle(ShapeKind.Rectangle, 0xFF6496C8, 0), new(10, 10, 60, 40))!;
        Assert.True(source.Copy());
        source.AssignColorProfile(RgbProfile.Srgb);
        var target = EditorSession.NewCanvas(128, 96, null, ColorDepth.Sixteen); target.AssignColorProfile(RgbProfile.DisplayP3);
        var state = target.History.CurrentId;
        var pasted = target.Paste()!;
        Assert.NotNull(pasted.Shape); Assert.Equal((uint)RgbProfile.Rgb1998.ConvertColor(new SKColor(0xFF6496C8), RgbProfile.DisplayP3), pasted.Shape.Fill);
        Assert.Equal(RgbProfile.DisplayP3, Pixels.ProfileOf(pasted.Pixels!));
        Assert.Equal(0xFF6496C8u, EditorSession.CopiedLayers!.Layers.Single().Shape!.Fill);
        target.Undo(); Assert.Equal(state, target.History.CurrentId); Assert.Single(target.Document.Layers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pixel_paste_converts_directly_between_profiles_and_keeps_original_precision(bool intoSelection)
    {
        using var image = Pixels.NewWorkingColor(512, 2, ColorDepth.Sixteen, RgbProfile.DisplayP3);
        var pixels = new PixelBuffer(image);
        for (var y = 0; y < 2; y++) for (var x = 0; x < 512; x++) pixels.Write(x, y, new((24000 + x) / 65535f, .6f, .2f, 1));
        var bytes = image.Bytes;
        var target = EditorSession.NewCanvas(512, 2, null, ColorDepth.Sixteen); target.AssignColorProfile(RgbProfile.Rgb1998);
        if (intoSelection) target.SelectAll();
        var clip = new ClipboardImage(image, new(0, 0));
        var pasted = (intoSelection ? target.PasteInto(clip) : target.Paste(clip))!;
        using var expected = Pixels.ConvertWorking(image, ColorDepth.Sixteen, target.Document.Profile);
        Assert.Equal(expected.Bytes, pasted.Pixels!.Bytes); Assert.Equal(bytes, image.Bytes);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(pasted.Pixels).Read(x, 0).R).Distinct().Count());
    }
}

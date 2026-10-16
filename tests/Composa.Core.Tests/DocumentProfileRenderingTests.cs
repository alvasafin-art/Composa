using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Composa.IO;

namespace Composa.Core.Tests;

public class DocumentProfileRenderingTests
{
    [Fact]
    public void Project_embeds_profiles_separately_and_keeps_nested_spaces_and_low_bits()
    {
        var document = new Document(512, 8) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var source = Pixels.NewColor(512, 8, ColorDepth.Sixteen, document.Profile.Space); var pixels = new PixelBuffer(source);
        for (var y = 0; y < 8; y++) for (var x = 0; x < 512; x++) pixels.Write(x, y, new((24000 + x) / 65535f, .6f, .2f, 1));
        document.Layers.Add(Layer.Raster("P3", source));
        var nested = new Document(8, 8) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.Rgb1998 };
        var inner = Pixels.NewColor(8, 8, ColorDepth.Sixteen, nested.Profile.Space); var innerPixels = new PixelBuffer(inner);
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) innerPixels.Write(x, y, new(.7f, .2f, .1f, 1));
        nested.Layers.Add(Layer.Raster("Inner RGB", inner));
        var smart = SmartObjectSource.Create(nested); var instance = Layer.Raster("RGB 1998 object", smart.Preview); instance.SmartObject = smart; document.Layers.Add(instance);
        using var stream = new MemoryStream(); ProjectFile.Write(document, stream);
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true)) Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("profiles/")));
        stream.Position = 0; var loaded = ProjectFile.Read(stream);
        Assert.Equal(document.Profile, loaded.Profile); Assert.Equal(nested.Profile, loaded.Layers[1].SmartObject!.OpenDocument().Profile);
        Assert.Equal(document.Profile, Pixels.ProfileOf(loaded.Layers[0].Pixels!));
        var loadedInner = loaded.Layers[1].SmartObject!.OpenDocument();
        for (var c = 0; c < 4; c++)
        {
            var originalValue = new PixelBuffer(inner).Read(4, 4)[c];
            var loadedValue = new PixelBuffer(loadedInner.Layers[0].Pixels!).Read(4, 4)[c];
            Assert.True(Math.Abs(originalValue - loadedValue) <= 2f / 65535, $"Inner source channel {c}: {originalValue} -> {loadedValue}");
        }
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(loaded.Layers[0].Pixels!).Read(x, 4).R).Distinct().Count());
        using var before = DocumentRenderer.Flatten(document); using var after = DocumentRenderer.Flatten(loaded);
        var a = new PixelBuffer(before); var b = new PixelBuffer(after);
        for (var x = 0; x < 512; x++) for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(a.Read(x, 4)[c] - b.Read(x, 4)[c]), 0, 2f / 65535);
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void Working_composite_keeps_profile_and_display_copy_converts_even_at_the_same_depth(ColorDepth depth)
    {
        var profile = RgbProfile.Rgb1998;
        var document = new Document(600, 600) { Depth = depth, Profile = profile };
        var source = Pixels.NewColor(600, 600, depth, profile.Space); var pixels = new PixelBuffer(source);
        for (var y = 0; y < 600; y++)
        for (var x = 0; x < 600; x++) pixels.Write(x, y, new(.5f, .7f, .2f, 1));
        document.Layers.Add(Layer.Raster("Wide RGB", source));
        using var flattened = DocumentRenderer.Flatten(document);
        Assert.True(RgbProfile.SameSpace(profile.Space, flattened.ColorSpace));
        Assert.InRange(Math.Abs(new PixelBuffer(flattened).Read(300, 300).R - .5), 0, 1d / 255);
        using var expected = Pixels.ConvertWorking(flattened, ColorDepth.Eight, RgbProfile.Srgb);
        using var display = Pixels.NewColor(600, 600);
        DocumentRenderer.Render(document, display, document.Bounds);
        Assert.Equal(expected.Bytes, display.Bytes);
        Assert.True(Math.Abs(display.GetPixel(300, 300).Red / 255d - .5) > .03);
        Assert.Same(profile, document.Clone().Profile);
    }

    [Fact]
    public void Multiply_and_group_siblings_blend_in_document_RGB_before_display_conversion()
    {
        var document = new Document(16, 12) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        SKBitmap Color(PixelValue color)
        {
            var image = Pixels.NewColor(16, 12, ColorDepth.Sixteen, document.Profile.Space); var pixels = new PixelBuffer(image);
            for (var y = 0; y < 12; y++) for (var x = 0; x < 16; x++) pixels.Write(x, y, color);
            return image;
        }
        document.Layers.Add(Layer.Raster("Bottom", Color(new(.4f, .5f, .6f, 1))));
        var group = Layer.Group("Isolated"); group.Blend = BlendMode.Normal; group.Blend = BlendMode.Multiply;
        group.Children.Add(Layer.Raster("Top", Color(new(.5f, .6f, .7f, 1)))); document.Layers.Add(group);
        using var result = DocumentRenderer.Flatten(document); var p = new PixelBuffer(result).Read(8, 6);
        Assert.InRange(Math.Abs(p.R - .2), 0, .000001); Assert.InRange(Math.Abs(p.G - .3), 0, .000001); Assert.InRange(Math.Abs(p.B - .42), 0, .000001);
        Assert.True(RgbProfile.SameSpace(document.Profile.Space, result.ColorSpace));
    }

    [Fact]
    public void Assign_preserves_numbers_and_conversion_preserves_display_colors_without_mutating_source()
    {
        using var source = Pixels.NewColor(512, 1, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(source);
        for (var x = 0; x < 512; x++) pixels.Write(x, 0, new((24000 + x) / 65535f, .6f, .2f, 1));
        var original = source.Bytes;
        using var assigned = Pixels.AssignProfile(source, RgbProfile.Rgb1998);
        Assert.Equal(original, assigned.Bytes);
        using var converted = Pixels.ConvertWorking(source, ColorDepth.Sixteen, RgbProfile.Rgb1998);
        Assert.NotEqual(original, converted.Bytes);
        using var before = Pixels.DisplayCopy(source); using var after = Pixels.DisplayCopy(converted);
        for (var i = 0; i < before.ByteCount; i++) Assert.InRange(Math.Abs(before.GetPixelSpan()[i] - after.GetPixelSpan()[i]), 0, 1);
        using var roundTrip = Pixels.ConvertWorking(converted, ColorDepth.Sixteen, RgbProfile.Srgb);
        // Two inverse float CMM passes have a small numerical error, still far below an RGB8 step.
        for (var x = 0; x < 512; x++) for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(new PixelBuffer(roundTrip).Read(x, 0)[c] - pixels.Read(x, 0)[c]), 0, 4f / 65535);
        Assert.Equal(original, source.Bytes);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => new PixelBuffer(converted).Read(x, 0).R).Distinct().Count());
    }
}

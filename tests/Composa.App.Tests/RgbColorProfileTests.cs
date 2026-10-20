using Composa.IO;
using Composa.Rendering;
using SkiaSharp;
using Magick = ImageMagick;

namespace Composa.App.Tests;

public class RgbColorProfileTests
{
    [Theory]
    [InlineData(Magick.MagickFormat.Png)]
    [InlineData(Magick.MagickFormat.Jpeg)]
    public void Embedded_Adobe_RGB_is_converted_on_import(Magick.MagickFormat format)
    {
        using var source = new Magick.MagickImage(new Magick.MagickColor("#80B432"), 16, 12);
        source.Depth = 8;
        source.Quality = 100;
        source.SetProfile(Magick.ColorProfiles.AdobeRGB1998);
        using var bytes = new MemoryStream(source.ToByteArray(format));
        using var imported = ImageFiles.Load(bytes);
        // The expected value comes from LittleCMS, independently of Skia's conversion engine.
        source.TransformColorSpace(Magick.ColorProfiles.SRGB);
        using var reference = SKBitmap.Decode(source.ToByteArray(Magick.MagickFormat.Png));
        var actual = imported.GetPixel(8, 6);
        var expected = reference.GetPixel(8, 6);
        Assert.InRange(Math.Abs(actual.Red - expected.Red), 0, 4);
        Assert.InRange(Math.Abs(actual.Green - expected.Green), 0, 4);
        Assert.InRange(Math.Abs(actual.Blue - expected.Blue), 0, 4);
        Assert.InRange(actual.Red, 90, 105); // Reject merely assigning sRGB to the unconverted #80B432.
    }

    [Theory]
    [InlineData(ExportFormat.Png, false)]
    [InlineData(ExportFormat.Png, true)]
    [InlineData(ExportFormat.Jpeg, false)]
    [InlineData(ExportFormat.Jpeg, true)]
    [InlineData(ExportFormat.Webp, false)]
    [InlineData(ExportFormat.Webp, true)]
    public void Export_has_a_readable_sRGB_profile_and_valid_transparency(ExportFormat format, bool transparent)
    {
        using var source = Pixels.NewColor(16, 12);
        source.Erase(new SKColor(80, 160, 240, transparent ? (byte)128 : (byte)255));
        var encoded = ImageFiles.Encode(source, format, quality: 100);
        using var independentDecoder = new Magick.MagickImage(encoded);
        var profile = independentDecoder.GetColorProfile();
        Assert.NotNull(profile);
        Assert.Contains("sRGB", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16U, independentDecoder.Width);
        Assert.Equal(12U, independentDecoder.Height);
        using var stream = new MemoryStream(encoded);
        using var decoded = ImageFiles.Load(stream);
        var pixel = decoded.GetPixel(8, 6);
        var preservesAlpha = transparent && format != ExportFormat.Jpeg;
        Assert.InRange((int)pixel.Alpha, preservesAlpha ? 127 : 255, preservesAlpha ? 129 : 255);
        var expected = transparent && format == ExportFormat.Jpeg ? new SKColor(167, 207, 247) : new SKColor(80, 160, 240);
        Assert.InRange(Math.Abs(pixel.Red - expected.Red), 0, 4);
        Assert.InRange(Math.Abs(pixel.Green - expected.Green), 0, 4);
        Assert.InRange(Math.Abs(pixel.Blue - expected.Blue), 0, 4);
    }
}

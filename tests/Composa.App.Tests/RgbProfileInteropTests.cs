using Composa.ColorManagement;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Magick = ImageMagick;

namespace Composa.App.Tests;

public class RgbProfileInteropTests
{
    [Fact]
    public void TIFF_decode_through_the_bundled_backend_preserves_wide_gamut_profile_and_RGB16()
    {
        var profile = RgbProfile.DisplayP3;
        using var source = Pixels.NewWorkingColor(512, 2, ColorDepth.Sixteen, profile);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        for (var i = 0; i < 1024; i++) { samples[i * 4] = (24000 + i % 512) / 65535f; samples[i * 4 + 1] = .7f; samples[i * 4 + 2] = .2f; samples[i * 4 + 3] = 1; }
        var path = Path.Combine(Path.GetTempPath(), "composa-profile-" + Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            ImageFiles.Save(source, path, ExportFormat.Tiff);
            using var png = new MemoryStream(BundledImageMagick.TryLoad()!.Convert(path, ImageMagickOutput.PngSource));
            using var decoded = ImageFiles.Load(png, preserveProfile: true);
            Assert.Equal(profile, Pixels.ProfileOf(decoded)); Assert.Equal(ColorDepth.Sixteen, Pixels.DepthOf(decoded));
            var result = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(decoded.GetPixelSpan());
            var levels = new HashSet<float>(); for (var x = 0; x < 512; x++) { levels.Add(result[x * 4]); Assert.InRange(Math.Abs(result[x * 4] - samples[x * 4]), 0, 1f / 65535); }
            Assert.Equal(512, levels.Count);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(ExportFormat.Png)]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Webp)]
    [InlineData(ExportFormat.Tiff)]
    public void Wide_gamut_export_keeps_the_actual_ICC_and_rgb_numbers(ExportFormat format)
    {
        var profile = RgbProfile.DisplayP3;
        using var space = SKColorSpace.CreateIcc(profile.IccBytes());
        using var source = Pixels.NewColor(32, 32, space: space);
        var pixels = source.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 128; pixels[i + 1] = 180; pixels[i + 2] = 50; pixels[i + 3] = 255; }
        var bytes = ImageFiles.Encode(source, format, quality: 100);
        using var independent = new Magick.MagickImage(bytes);
        Assert.Equal(profile.IccBytes(), independent.GetColorProfile()!.ToByteArray());
        using (var raw = independent.GetPixels())
        {
            var value = raw.GetPixel(16, 16).ToArray();
            Assert.InRange(Math.Abs(value[0] / 257 - 128), 0, 3); Assert.InRange(Math.Abs(value[1] / 257 - 180), 0, 3); Assert.InRange(Math.Abs(value[2] / 257 - 50), 0, 3);
        }
        independent.TransformColorSpace(Magick.ColorProfiles.SRGB);
        using var display = Pixels.DisplayCopy(source); using var colors = independent.GetPixels(); var expected = colors.GetPixel(16, 16).ToArray();
        var actual = display.GetPixel(16, 16);
        Assert.InRange(Math.Abs(actual.Red - expected[0] / 257), 0, 4); Assert.InRange(Math.Abs(actual.Green - expected[1] / 257), 0, 4); Assert.InRange(Math.Abs(actual.Blue - expected[2] / 257), 0, 4);
        if (format != ExportFormat.Tiff)
        {
            using var input = new MemoryStream(bytes); using var preserved = ImageFiles.Load(input, preserveProfile: true);
            Assert.Equal(profile, Pixels.ProfileOf(preserved)); Assert.InRange(Math.Abs(preserved.GetPixel(16, 16).Red - 128), 0, 3);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_wide_gamut_profiles_agree_with_independent_LittleCMS_conversion(bool p3)
    {
        var profile = p3 ? RgbProfile.DisplayP3 : RgbProfile.Rgb1998;
        using var space = SKColorSpace.CreateIcc(profile.IccBytes());
        using var source = Pixels.NewColor(64, 4, ColorDepth.Sixteen, space);
        var raw = new byte[64 * 4 * 8];
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 64; x++)
        for (var c = 0; c < 4; c++)
        {
            var value = (ushort)(c == 3 ? y == 0 ? 65535 : 30001 : (c == 0 ? 24000 : c == 1 ? 46000 : 13000) + x);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(((y * 64 + x) * 4 + c) * 2), value);
            values[(y * 64 + x) * 4 + c] = value / 65535f * (c == 3 ? 1 : y == 0 ? 1 : 30001 / 65535f);
        }
        using var converted = Pixels.NewColor(64, 4, ColorDepth.Sixteen);
        using (var canvas = new SKCanvas(converted)) canvas.DrawBitmap(source, 0, 0);
        using var reference = new Magick.MagickImage();
        reference.Read(raw, new Magick.MagickReadSettings { Width = 64, Height = 4, Depth = 16, Endian = Magick.Endian.MSB, Format = Magick.MagickFormat.Rgba });
        reference.SetProfile(new Magick.ColorProfile(profile.IccBytes()));
        reference.RenderingIntent = Magick.RenderingIntent.Relative;
        reference.TransformColorSpace(new Magick.ColorProfile(RgbProfile.Srgb.IccBytes()), Magick.ColorTransformMode.HighRes);
        using var pixels = reference.GetPixels();
        var actual = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(converted.GetPixelSpan());
        var reds = new HashSet<float>();
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 64; x++)
        {
            var expected = pixels.GetPixel(x, y).ToArray();
            var alpha = actual[(y * 64 + x) * 4 + 3];
            // The float raster backend retains extended values outside the destination gamut;
            // unsigned integer RGB output clips them, as LittleCMS Q16 does.
            for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(Math.Clamp(actual[(y * 64 + x) * 4 + c] / alpha, 0, 1) - expected[c] / 65535f), 0, .001);
            Assert.InRange(Math.Abs(alpha - expected[3] / 65535f), 0, .000002);
            if (y == 0) reds.Add(actual[x * 4]);
        }
        Assert.Equal(64, reds.Count);
    }
}

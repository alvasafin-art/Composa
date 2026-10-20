using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Magick = ImageMagick;

namespace Composa.App.Tests;

public class HighPrecisionTiffTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Auto_import_retains_declared_TIFF_depth_even_when_flat_color_fits_in_8_bits(int depth)
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-test-" + Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            using (var image = new Magick.MagickImage(Magick.MagickColors.Red, 16, 12)) { image.Depth = (uint)depth; image.Write(path); }
            var backend = BundledImageMagick.TryLoad()!;
            using var input = new MemoryStream(backend.Convert(path, ImageMagickOutput.PngSource));
            Assert.Equal(depth, input.ToArray()[24]);
            using var decoded = ImageFiles.Load(input);
            Assert.Equal(depth == 16 ? SKColorType.RgbaF32 : SKColorType.Rgba8888, decoded.ColorType);
            Assert.Equal(SKColors.Red, decoded.GetPixel(4, 4));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(ColorDepth.Eight)]
    [InlineData(ColorDepth.Sixteen)]
    public void TIFF_export_has_true_samples_straight_alpha_ICC_and_resolution(ColorDepth depth)
    {
        using var image = Pixels.NewColor(512, 2, depth);
        if (depth == ColorDepth.Sixteen)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
            for (var y = 0; y < 2; y++)
            for (var x = 0; x < 512; x++)
            {
                var i = (y * 512 + x) * 4;
                var alpha = y == 0 ? 1 : 30001 / 65535f;
                for (var c = 0; c < 3; c++) samples[i + c] = (24000 + x + c * 1000) / 65535f * alpha;
                samples[i + 3] = alpha;
            }
        }
        else image.Erase(new SKColor(100, 150, 200, 128));
        var path = Path.Combine(Path.GetTempPath(), "composa-test-" + Guid.NewGuid().ToString("N") + ".tiff");
        try
        {
            ImageFiles.Save(image, path, ExportFormat.Tiff, resolution: 300);
            using var decoded = new Magick.MagickImage(path);
            Assert.Equal((uint)depth, decoded.Depth);
            Assert.Equal(300, decoded.Density.X); Assert.Equal(300, decoded.Density.Y);
            Assert.NotNull(decoded.GetColorProfile());
            Assert.Equal(ExportFormat.Tiff, ImageFiles.FormatFor(path));
            using var pixels = decoded.GetPixels();
            if (depth == ColorDepth.Sixteen)
            {
                var reds = new HashSet<ushort>();
                for (var y = 0; y < 2; y++)
                for (var x = 0; x < 512; x++)
                {
                    var p = pixels.GetPixel(x, y).ToArray();
                    for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(p[c] - (24000 + x + c * 1000)), 0, 1);
                    Assert.InRange(Math.Abs(p[3] - (y == 0 ? 65535 : 30001)), 0, 1);
                    if (y == 0) reds.Add(p[0]);
                }
                Assert.Equal(512, reds.Count);
            }
            else
            {
                var p = pixels.GetPixel(0, 0).ToArray();
                Assert.InRange(Math.Abs(p[0] / 257 - 100), 0, 1);
                Assert.Equal(128 * 257, p[3]);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_precision_TIFF_decode_retains_ICC_and_alpha(bool tagged)
    {
        var backend = BundledImageMagick.TryLoad()!;
        var path = Path.Combine(Path.GetTempPath(), "composa-test-" + Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            var raw = new byte[512 * 8];
            for (var x = 0; x < 512; x++)
            for (var c = 0; c < 4; c++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan((x * 4 + c) * 2), (ushort)(c == 3 ? 30001 : (c == 0 ? 33000 : c == 1 ? 46000 : 13000) + x));
            using (var image = new Magick.MagickImage())
            {
                image.Read(raw, new Magick.MagickReadSettings { Width = 512, Height = 1, Depth = 16, Endian = Magick.Endian.MSB, Format = Magick.MagickFormat.Rgba });
                image.SetProfile(tagged ? Magick.ColorProfiles.AdobeRGB1998 : Magick.ColorProfiles.SRGB);
                image.Depth = 16; image.Write(path);
            }
            using var converted = new MemoryStream(backend.Convert(path, ImageMagickOutput.Png16));
            using var result = ImageFiles.Load(converted, "TIFF", ColorDepth.Sixteen);
            Assert.Equal(SKColorType.RgbaF32, result.ColorType);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(result.GetPixelSpan());
            var reds = new HashSet<float>();
            using var reference = new Magick.MagickImage(path);
            if (tagged) reference.TransformColorSpace(Magick.ColorProfiles.SRGB);
            using var expected = reference.GetPixels();
            for (var x = 0; x < 512; x++)
            {
                var alpha = pixels[x * 4 + 3];
                Assert.InRange(Math.Abs(alpha - 30001 / 65535f), 0, .000002);
                for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(pixels[x * 4 + c] / alpha - expected.GetPixel(x, 0).ToArray()[c] / 65535f), 0, .005);
                reds.Add(pixels[x * 4]);
            }
            Assert.Equal(512, reds.Count);
            var developed = RawImage.ParsePam(backend.Convert(path, ImageMagickOutput.Pam16));
            Assert.InRange(Math.Abs(developed.Samples[0] - expected.GetPixel(0, 0).ToArray()[0]), 0, 2);
        }
        finally { File.Delete(path); }
    }
}

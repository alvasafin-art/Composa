using Composa.ColorManagement;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Magick = ImageMagick;

namespace Composa.App.Tests;

public class RgbProfileInteropTests
{
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
        reference.SetProfile(new Magick.ColorProfile(profile.IccBytes())); reference.TransformColorSpace(Magick.ColorProfiles.SRGB);
        using var pixels = reference.GetPixels();
        var actual = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(converted.GetPixelSpan());
        var reds = new HashSet<float>();
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 64; x++)
        {
            var expected = pixels.GetPixel(x, y).ToArray();
            var alpha = actual[(y * 64 + x) * 4 + 3];
            for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(actual[(y * 64 + x) * 4 + c] / alpha - expected[c] / 65535f), 0, .005);
            Assert.InRange(Math.Abs(alpha - expected[3] / 65535f), 0, .000002);
            if (y == 0) reds.Add(actual[x * 4]);
        }
        Assert.Equal(64, reds.Count);
    }
}

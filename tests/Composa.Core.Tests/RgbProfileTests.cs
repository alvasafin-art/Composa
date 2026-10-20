using System.Buffers.Binary;
using Composa.ColorManagement;

namespace Composa.Core.Tests;

public class RgbProfileTests
{
    [Fact]
    public void Built_in_profiles_are_valid_embeddable_immutable_RGB_ICC_with_real_transfer_functions()
    {
        foreach (var profile in RgbProfile.BuiltIn)
        {
            var bytes = profile.IccBytes();
            Assert.Equal(bytes.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes));
            Assert.Equal("RGB ", System.Text.Encoding.ASCII.GetString(bytes, 16, 4));
            Assert.Equal(profile, RgbProfile.FromIcc(bytes));
            Assert.False(string.IsNullOrWhiteSpace(RgbProfile.FromIcc(bytes).Name));
            Assert.True(profile.Space.IsNumericalTransferFunction);
            bytes[0] ^= 1;
            Assert.NotEqual(bytes, profile.IccBytes());
        }
        Assert.False(SkiaSharp.SKColorSpace.Equal(RgbProfile.Srgb.Space, RgbProfile.DisplayP3.Space));
        Assert.False(SkiaSharp.SKColorSpace.Equal(RgbProfile.Srgb.Space, RgbProfile.Rgb1998.Space));
        foreach (var pair in new[] { (RgbProfile.DisplayP3, SkiaSharp.SKColorSpaceXyz.DisplayP3), (RgbProfile.Rgb1998, SkiaSharp.SKColorSpaceXyz.AdobeRgb) })
        {
            var actual = pair.Item1.Space.ToColorSpaceXyz().Values; var expected = pair.Item2.Values;
            for (var i = 0; i < 9; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, .0005);
        }
    }

    [Fact]
    public void Invalid_truncated_and_non_RGB_profiles_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => RgbProfile.FromIcc(new byte[32]));
        var bytes = RgbProfile.Srgb.IccBytes();
        Assert.Throws<InvalidDataException>(() => RgbProfile.FromIcc(bytes.AsSpan(0, bytes.Length - 1)));
        "CMYK"u8.CopyTo(bytes.AsSpan(16));
        Assert.Throws<InvalidDataException>(() => RgbProfile.FromIcc(bytes));
    }
}

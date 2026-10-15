using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class PngPrecisionTests
{
    [Fact]
    public void Embedded_Adobe_RGB_is_converted_in_float_against_an_independent_LittleCMS_reference()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision");
        using var input = File.OpenRead(Path.Combine(root, "png16-adobe-rgb.png"));
        using var referenceInput = File.OpenRead(Path.Combine(root, "png16-srgb-reference.png"));
        using var actual = Png16Codec.TryDecode(input)!;
        using var reference = Png16Codec.TryDecode(referenceInput)!;
        var colors = new PixelBuffer(actual); var expected = new PixelBuffer(reference);
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
        {
            var a = colors.Read(x, y); var b = expected.Read(x, y);
            Assert.InRange(Math.Abs(a.R - b.R), 0, .005);
            Assert.InRange(Math.Abs(a.G - b.G), 0, .005);
            Assert.InRange(Math.Abs(a.B - b.B), 0, .005);
        }
        // Conversion is not a profile assignment: red changes materially from the source's encoded value.
        Assert.True(Math.Abs(colors.Read(0, 0).R - 33000 / 65535f) > .05);
        Assert.True(Enumerable.Range(0, 17).Select(x => colors.Read(x, 0).R).Distinct().Count() == 17);
    }

    private static string Fixture(int type, bool interlaced) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision", $"png16-{type}-{(interlaced ? "adam7" : "plain")}.png");

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void Independent_16_bit_PNG_fixtures_keep_the_low_bits_in_all_color_types_and_Adam7(int type, bool interlaced)
    {
        var bytes = File.ReadAllBytes(Fixture(type, interlaced));
        Assert.Equal(16, bytes[24]); Assert.Equal(type, bytes[25]); Assert.Equal(interlaced ? 1 : 0, bytes[28]);
        using var stream = new MemoryStream(bytes);
        using var decoded = Png16Codec.TryDecode(stream)!;
        Assert.NotNull(decoded);
        Assert.Equal(SKColorType.RgbaF32, decoded.ColorType);
        var pixels = new PixelBuffer(decoded);
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 17; x++)
        {
            var expectedR = (24000 + x * 19 + y * 13) / 65535f;
            var expectedG = type is 0 or 4 ? expectedR : (24000 + x * 19 + y * 13 + 401) / 65535f;
            var expectedB = type is 0 or 4 ? expectedR : (24000 + x * 19 + y * 13 + 802) / 65535f;
            var expectedA = type is 4 or 6 ? (10000 + (x * 173 + y * 531) % 50000) / 65535f : 1;
            var actual = pixels.Read(x, y).Straight;
            Assert.InRange(Math.Abs(actual.R - expectedR), 0, 0.000002);
            Assert.InRange(Math.Abs(actual.G - expectedG), 0, 0.000002);
            Assert.InRange(Math.Abs(actual.B - expectedB), 0, 0.000002);
            Assert.InRange(Math.Abs(actual.A - expectedA), 0, 0.000002);
        }
    }

    [Fact]
    public void Float_working_pixels_and_PNG16_round_trip_keep_512_adjacent_levels()
    {
        using var source = Pixels.NewColor(512, 1, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(source);
        for (var x = 0; x < 512; x++)
        {
            var value = (24000 + x) / 65535f;
            pixels.Write(x, 0, new(value, value, value, 1));
        }
        using var copy = Pixels.NewLike(source);
        using (var canvas = new SKCanvas(copy)) canvas.DrawBitmap(source, 0, 0);
        Assert.Equal(source.Bytes, copy.Bytes);
        var encoded = ImageFiles.Encode(copy, ExportFormat.Png);
        Assert.Equal(16, encoded[24]);
        using var input = new MemoryStream(encoded);
        using var decoded = Png16Codec.TryDecode(input)!;
        var output = new PixelBuffer(decoded);
        Assert.Equal(512, Enumerable.Range(0, 512).Select(x => output.Read(x, 0).R).Distinct().Count());
        for (var x = 0; x < 512; x++) Assert.InRange(Math.Abs(output.Read(x, 0).R * 65535 - 24000 - x), 0, .02);
        using var display = Pixels.DisplayCopy(source);
        Assert.InRange(Enumerable.Range(0, 512).Select(x => display.GetPixel(x, 0).Red).Distinct().Count(), 2, 4);
    }

    [Fact]
    public void Damaged_16_bit_chunk_is_refused_and_an_8_bit_stream_is_left_for_the_normal_decoder()
    {
        var bytes = File.ReadAllBytes(Fixture(6, false));
        bytes[30] ^= 1;
        using var corrupt = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => Png16Codec.TryDecode(corrupt));
        using var eight = Pixels.NewColor(4, 4);
        using var input = new MemoryStream(ImageFiles.Encode(eight, ExportFormat.Png));
        Assert.Null(Png16Codec.TryDecode(input));
        Assert.Equal(0, input.Position);
        using var decoded = ImageFiles.Load(input);
        Assert.Equal(4, decoded.Width);
    }
}

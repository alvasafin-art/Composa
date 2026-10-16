using System.Buffers.Binary;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class HighPrecisionPsdTests
{
    [Fact]
    public void ICC_conversion_keeps_low_bits_and_matches_LittleCMS()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision");
        var import = PsdImport.Load(Path.Combine(root, "rgb16-adobe-rgb.psd"));
        try
        {
            using var input = File.OpenRead(Path.Combine(root, "png16-srgb-reference.png"));
            using var reference = Composa.IO.Png16Codec.TryDecode(input)!;
            using var actual = DocumentRenderer.Flatten(import.ToDocument());
            var a = new PixelBuffer(actual); var e = new PixelBuffer(reference);
            for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
            for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(a.Read(x, y)[c] - e.Read(x, y)[c]), 0, .005);
            Assert.Equal(17, Enumerable.Range(0, 17).Select(x => a.Read(x, 0).R).Distinct().Count());
        }
        finally { import.Discard(); }
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(8, 3)]
    [InlineData(16, 2)]
    [InlineData(16, 3)]
    public void Merged_ZIP_and_prediction_decode_low_bits_and_alpha(int depth, int compression)
    {
        const int width = 9, height = 3;
        var document = new Document(width, height) { Depth = depth == 16 ? ColorDepth.Sixteen : ColorDepth.Eight };
        var image = Pixels.NewColor(width, height, document.Depth);
        var pixels = new PixelBuffer(image);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) pixels.Write(x, y, new PixelValue((24000 + x * 257 + y) / 65535f, .4f, .6f, .75f).Premultiplied);
        document.Layers.Add(Layer.Raster("Layer", image));
        using var original = new MemoryStream(); PsdExport.Write(document, original);
        var bytes = original.ToArray();
        var offset = 26;
        for (var section = 0; section < 3; section++) offset += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset)));
        var counts = new int[4 * height];
        for (var i = 0; i < counts.Length; i++) counts[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2 + i * 2));
        var byteWidth = width * (depth / 8);
        var plane = new byte[byteWidth * height * 4];
        PsdChannels.UnpackRows(bytes.AsSpan(offset + 2 + counts.Length * 2), counts, byteWidth, height * 4, plane, 0);
        if (compression == 3)
        {
            for (var y = 0; y < height * 4; y++)
            for (var x = width - 1; x > 0; x--)
            {
                var i = y * byteWidth + x * (depth / 8);
                if (depth == 16)
                {
                    var current = BinaryPrimitives.ReadUInt16BigEndian(plane.AsSpan(i));
                    var previous = BinaryPrimitives.ReadUInt16BigEndian(plane.AsSpan(i - 2));
                    BinaryPrimitives.WriteUInt16BigEndian(plane.AsSpan(i), unchecked((ushort)(current - previous)));
                }
                else plane[i] = unchecked((byte)(plane[i] - plane[i - 1]));
            }
        }
        using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, offset));
        output.WriteByte(0); output.WriteByte((byte)compression);
        using (var zip = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zip.Write(plane);
        var imported = PsdImport.LoadAppearance(output.ToArray(), DocumentLimits.DocumentPixelBudget);
        var expectedImport = PsdImport.LoadAppearance(bytes, DocumentLimits.DocumentPixelBudget);
        try { Assert.Equal(expectedImport.Layers[0].Pixels!.Bytes, imported.Layers[0].Pixels!.Bytes); }
        finally { imported.Discard(); expectedImport.Discard(); }
    }

    [Fact]
    public void Independent_Q16_PSD_layer_preserves_color_and_alpha()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision", "rgb16-alpha.psd"));
        // ImageMagick's writer leaves its merged RGB unmatted. Its own default reader turns
        // the second merged row black; the independently encoded layer channels are authoritative.
        var import = PsdImport.Load(bytes);
        try
        {
            var document = import.ToDocument();
            Assert.Equal(ColorDepth.Sixteen, document.Depth);
            Assert.DoesNotContain(import.Conversions, c => c.Message.Contains("8-bit channels"));
            using var result = DocumentRenderer.Flatten(document);
            var pixels = new PixelBuffer(result);
            Assert.Equal(512, Enumerable.Range(0, 512).Select(x => pixels.Read(x, 0).R).Distinct().Count());
            for (var y = 0; y < 2; y++)
            for (var x = 0; x < 512; x++)
            {
                var p = pixels.Read(x, y).Straight;
                for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(p[c] - (24000 + x + c * 1000) / 65535f), 0, 2f / 65535);
                Assert.InRange(Math.Abs(p.A - (y == 0 ? 1 : 32767 / 65535f)), 0, 1f / 65535);
            }
        }
        finally { import.Discard(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Published_high_depth_PSD_matches_independent_decoder(bool appearance)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision", "photoshop-16bit5x5.psd"));
        var import = appearance ? PsdImport.LoadAppearance(bytes, DocumentLimits.DocumentPixelBudget) : PsdImport.Load(bytes);
        try
        {
            using var image = DocumentRenderer.Flatten(import.ToDocument());
            var value = new PixelBuffer(image).Read(2, 2).Straight;
            // Independent ImageMagick Q16 values, including low bytes that an 8-bit path loses.
            Assert.InRange(Math.Abs(value.R - 60539 / 65535f), 0, 1f / 65535);
            Assert.InRange(Math.Abs(value.G - 62141 / 65535f), 0, 1f / 65535);
            Assert.InRange(Math.Abs(value.B - 64391 / 65535f), 0, 1f / 65535);
        }
        finally { import.Discard(); }
    }

    [Fact]
    public void Layered_and_merged_export_preserve_16bit_samples_masks_and_mixed_depths()
    {
        var document = new Document(512, 2) { Depth = ColorDepth.Sixteen };
        var image = Pixels.NewColor(512, 2, ColorDepth.Sixteen);
        var pixels = new PixelBuffer(image);
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 512; x++)
        {
            var a = y == 0 ? 1 : 20001 / 65535f;
            pixels.Write(x, y, new PixelValue((24000 + x) / 65535f, (30000 + x) / 65535f, (40000 + x) / 65535f, a).Premultiplied);
        }
        var layer = Layer.Raster("Precise", image); layer.Mask = Pixels.NewMask(512, 2, 200);
        document.Layers.Add(layer);
        var hidden = Layer.Raster("8-bit source", Pixels.NewColor(1, 1)); hidden.Visible = false; document.Layers.Add(hidden);
        using var stream = new MemoryStream(); PsdExport.Write(document, stream);
        var bytes = stream.ToArray();
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22)));
        Assert.Contains("Lr16", System.Text.Encoding.ASCII.GetString(bytes));
        using var expected = DocumentRenderer.Flatten(document);
        foreach (var appearance in new[] { false, true })
        {
            var imported = appearance ? PsdImport.LoadAppearance(bytes, DocumentLimits.DocumentPixelBudget) : PsdImport.Load(bytes);
            try
            {
                using var actual = DocumentRenderer.Flatten(imported.ToDocument());
                var a = new PixelBuffer(actual); var e = new PixelBuffer(expected);
                for (var y = 0; y < 2; y++)
                for (var x = 0; x < 512; x++)
                for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(a.Read(x, y)[c] - e.Read(x, y)[c]), 0, 3f / 65535);
                // A merged U16 appearance rounds after mask multiplication; source channels retain every level.
                if (!appearance)
                {
                    var source = new PixelBuffer(imported.Layers[0].Pixels!);
                    Assert.Equal(512, Enumerable.Range(0, 512).Select(x => source.Read(x, 0).R).Distinct().Count());
                }
                Assert.True(Enumerable.Range(0, 512).Select(x => a.Read(x, 0).R).Distinct().Count() >= 390);
            }
            finally { imported.Discard(); }
        }
    }

    [Fact]
    public void High_depth_import_checks_real_byte_budget_before_decoding()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Precision", "rgb16-alpha.psd"));
        Assert.Throws<PsdException>(() => PsdImport.Load(bytes, 512 * 2));
        Assert.Throws<PsdException>(() => PsdImport.LoadAppearance(bytes, 512 * 2));
    }
}

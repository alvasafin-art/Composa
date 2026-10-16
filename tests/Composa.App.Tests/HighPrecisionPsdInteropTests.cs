using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Magick = ImageMagick;

namespace Composa.App.Tests;

public class HighPrecisionPsdInteropTests
{
    [Fact]
    public void Independent_reader_sees_full_precision_and_unmattes_merged_alpha()
    {
        var document = new Document(512, 2) { Depth = ColorDepth.Sixteen };
        var image = Pixels.NewColor(512, 2, ColorDepth.Sixteen);
        // Public bitmap access keeps this an independent exchange test, outside the core's pixel helper.
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 512; x++)
        {
            var i = (y * 512 + x) * 4;
            var alpha = y == 0 ? 1 : 32767 / 65535f;
            for (var c = 0; c < 3; c++) pixels[i + c] = (24000 + x + c * 1000) / 65535f * alpha;
            pixels[i + 3] = alpha;
        }
        document.Layers.Add(Layer.Raster("Precise", image));
        using var stream = new MemoryStream(); PsdExport.Write(document, stream);
        using var pages = new Magick.MagickImageCollection(stream.ToArray());
        Assert.Equal(2, pages.Count);
        foreach (var page in pages)
        {
            Assert.Equal(16u, page.Depth);
            using var decoded = page.GetPixels();
            var reds = new HashSet<ushort>();
            for (var y = 0; y < 2; y++)
            for (var x = 0; x < 512; x++)
            {
                var p = decoded.GetPixel(x, y).ToArray();
                for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(p[c] - (24000 + x + c * 1000)), 0, 2);
                Assert.InRange(Math.Abs(p[3] - (y == 0 ? 65535 : 32767)), 0, 1);
                if (y == 0) reds.Add(p[0]);
            }
            Assert.Equal(512, reds.Count);
        }
    }
}

using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

public sealed partial class LiquifyField
{
    /// <summary>An owned working-depth/profile image sampled from the immutable original, once.
    /// An identity full-size result is a byte-exact copy, including untouched float pixels.</summary>
    public SKBitmap Render(SKBitmap source, int maxSide = 0, CancellationToken token = default)
    {
        if (source.Width != Width || source.Height != Height || source.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32))
            throw new ArgumentException("The Liquify source must match its working RGB grid.", nameof(source));
        if (maxSide < 0 || maxSide > Model.DocumentLimits.MaxSide) throw new ArgumentOutOfRangeException(nameof(maxSide));
        token.ThrowIfCancellationRequested();
        var scale = maxSide == 0 ? 1 : Math.Min(1, (double)maxSide / Math.Max(Width, Height));
        var width = Math.Max(1, (int)Math.Round(Width * scale)); var height = Math.Max(1, (int)Math.Round(Height * scale));
        var full = width == Width && height == Height;
        var result = full ? Pixels.Clone(source) : Pixels.NewLike(source, width, height);
        SKBitmap? reduced = null;
        try
        {
            if (full && !HasDeformation) return result;
            var sample = source;
            if (!full)
            {
                var level = Math.Max(0, (int)Math.Floor(Math.Log2(1 / scale)));
                if (level > 0)
                {
                    var image = Pixels.Level(source, ref level);
                    if (level > 0)
                    {
                        sample = reduced = Pixels.NewLike(source, image.Width, image.Height);
                        if (!image.ReadPixels(reduced.Info, reduced.GetPixels(), reduced.RowBytes, 0, 0)) throw new InvalidOperationException("The Liquify preview source could not be read.");
                    }
                }
            }
            var input = new PixelBuffer(sample); var output = new PixelBuffer(result);
            var sx = (double)Width / width; var sy = (double)Height / height;
            var mx = (double)input.Width / Width; var my = (double)input.Height / Height;
            Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    if ((x & 255) == 0) token.ThrowIfCancellationRequested();
                    var px = (x + .5) * sx - .5; var py = (y + .5) * sy - .5;
                    var flow = full ? DisplacementAt(x, y) : SampleFlow((float)px, (float)py);
                    if (full && flow == default) continue;
                    var sampleX = (px + .5 + flow.X) * mx - .5; var sampleY = (py + .5 + flow.Y) * my - .5;
                    output.Write(x, y, Sample(input, sampleX, sampleY));
                }
            });
            Pixels.Invalidate(result); GC.KeepAlive(source); GC.KeepAlive(sample); return result;
        }
        catch { result.Dispose(); throw; }
        finally { reduced?.Dispose(); }
    }

    private static PixelValue Sample(PixelBuffer pixels, double x, double y)
    {
        x = Math.Clamp(x, 0, pixels.Width - 1); y = Math.Clamp(y, 0, pixels.Height - 1);
        var left = (int)x; var top = (int)y; var right = Math.Min(left + 1, pixels.Width - 1); var bottom = Math.Min(top + 1, pixels.Height - 1);
        var fx = (float)(x - left); var fy = (float)(y - top);
        return (pixels.Read(left, top) * (1 - fx) + pixels.Read(right, top) * fx) * (1 - fy)
            + (pixels.Read(left, bottom) * (1 - fx) + pixels.Read(right, bottom) * fx) * fy;
    }

    /// <summary>An owned Alpha8 preview of protection, with the unselected area included.</summary>
    public unsafe SKBitmap FreezePreview(int width, int height, SKBitmap? selection = null)
    {
        if (width < 1 || height < 1 || width > Width || height > Height) throw new ArgumentOutOfRangeException(nameof(width));
        if (selection != null && (selection.ColorType != SKColorType.Alpha8 || selection.Width != Width || selection.Height != Height))
            throw new ArgumentException("Expected an Alpha8 source-grid selection.", nameof(selection));
        var result = Pixels.NewMask(width, height); var target = (byte*)result.GetPixels(); var stride = result.RowBytes;
        var mask = selection == null ? null : (byte*)selection.GetPixels(); var maskStride = selection?.RowBytes ?? 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var px = Math.Min(Width - 1, (int)((x + .5) * Width / width)); var py = Math.Min(Height - 1, (int)((y + .5) * Height / height));
            var coverage = FrozenAt(px, py) / 255.0; var selected = mask == null ? 1 : mask[(long)py * maskStride + px] / 255.0;
            target[(long)y * stride + x] = (byte)Math.Round((1 - (1 - coverage) * selected) * 255);
        }
        Pixels.Invalidate(result); GC.KeepAlive(selection); return result;
    }
}

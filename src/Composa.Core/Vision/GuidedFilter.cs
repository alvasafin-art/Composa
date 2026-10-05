using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>
/// Brings a model's small matte up to the picture's size along the picture's own edges. The model sees a few hundred
/// pixels a side, so scaling its answer up plainly gives a blurry outline a dozen pixels wide; the guided filter
/// (He, Sun and Tang, 2010) instead fits the matte as a linear function of the picture's brightness in every
/// window, so where the picture has an edge the matte follows it. The fit is made on a reduced copy and applied at
/// full size, the fast variant from the same authors' 2015 paper, which keeps the cost that of one pass over the pixels.
/// </summary>
public static class GuidedFilter
{
    /// <summary>The longest side of the reduced copy the fit is made on.</summary>
    public const int WorkSize = 1024;

    /// <summary>
    /// The matte <paramref name="coarse"/> (0 to 1, <paramref name="coarseWidth"/> by <paramref name="coarseHeight"/>,
    /// covering the whole of <paramref name="guide"/>) at the guide's size, as an <c>Alpha8</c> mask. The guide is
    /// premultiplied color; a transparent pixel is always backdrop.
    /// </summary>
    public static unsafe SKBitmap Upsample(float[] coarse, int coarseWidth, int coarseHeight, SKBitmap guide, CancellationToken cancellation = default)
    {
        int width = guide.Width, height = guide.Height;
        var scale = Math.Min(1.0, (double)WorkSize / Math.Max(width, height));
        int workW = Math.Max(1, (int)Math.Round(width * scale)), workH = Math.Max(1, (int)Math.Round(height * scale));
        // The window must span the matte's own blur, which is a couple of the model's pixels wide, or the fit
        // cannot pull a soft transition back to the picture's edge; a wider one copies more of the picture's
        // texture into the matte, which epsilon damps.
        var radius = Math.Clamp((int)Math.Round(2.0 * Math.Max(workW, workH) / Math.Max(coarseWidth, coarseHeight)), 4, 48);
        const float epsilon = 0.01f;

        // The guide's brightness at work size. Premultiplied color is the color as seen over black, which is also
        // what the model saw, so the brightness is read straight from the channels.
        var gray = Brightness(guide, workW, workH);
        var p = Resample(coarse, coarseWidth, coarseHeight, workW, workH);
        cancellation.ThrowIfCancellationRequested();

        // The window means that the linear fit needs, each a box filter over the work image.
        var meanI = Box(gray, workW, workH, radius);
        var meanP = Box(p, workW, workH, radius);
        var ip = new float[workW * workH];
        var ii = new float[workW * workH];
        for (var i = 0; i < ip.Length; i++) { ip[i] = gray[i] * p[i]; ii[i] = gray[i] * gray[i]; }
        var corrIp = Box(ip, workW, workH, radius);
        var corrII = Box(ii, workW, workH, radius);
        var a = new float[workW * workH];
        var b = new float[workW * workH];
        for (var i = 0; i < a.Length; i++)
        {
            var variance = corrII[i] - meanI[i] * meanI[i];
            var covariance = corrIp[i] - meanI[i] * meanP[i];
            a[i] = covariance / (variance + epsilon);
            b[i] = meanP[i] - a[i] * meanI[i];
        }
        var meanA = Box(a, workW, workH, radius);
        var meanB = Box(b, workW, workH, radius);
        cancellation.ThrowIfCancellationRequested();

        // Applied at full size: the matte is a·brightness + b, with a and b read smoothly from the work grid.
        var result = Pixels.NewMask(width, height);
        var src = (byte*)guide.GetPixels();
        var dst = (byte*)result.GetPixels();
        int srcStride = guide.RowBytes, dstStride = result.RowBytes;
        double sx = (double)workW / width, sy = (double)workH / height;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            var row = src + (long)y * srcStride;
            var outRow = dst + (long)y * dstStride;
            var v = (y + 0.5) * sy - 0.5;
            for (var x = 0; x < width; x++)
            {
                var pixel = row + x * 4;
                if (pixel[3] < 8) { outRow[x] = 0; continue; }
                var u = (x + 0.5) * sx - 0.5;
                var brightness = (0.2126f * pixel[0] + 0.7152f * pixel[1] + 0.0722f * pixel[2]) / 255f;
                var q = Bilinear(meanA, workW, workH, u, v) * brightness + Bilinear(meanB, workW, workH, u, v);
                outRow[x] = (byte)Math.Clamp((int)MathF.Round(q * 255f), 0, 255);
            }
        });
        return result;
    }

    /// <summary>The picture's brightness, 0 to 1, drawn at the work size.</summary>
    private static unsafe float[] Brightness(SKBitmap guide, int workW, int workH)
    {
        using var small = Pixels.NewColor(workW, workH);
        if (workW == guide.Width && workH == guide.Height) Pixels.CopyPixels(guide, small);
        else
        {
            using var canvas = new SKCanvas(small);
            using var image = SKImage.FromPixels(guide.PeekPixels());
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(image, new SKRect(0, 0, workW, workH), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        var gray = new float[workW * workH];
        var pixels = (byte*)small.GetPixels();
        for (var y = 0; y < workH; y++)
        {
            var row = pixels + (long)y * small.RowBytes;
            for (var x = 0; x < workW; x++)
            {
                var p = row + x * 4;
                gray[y * workW + x] = (0.2126f * p[0] + 0.7152f * p[1] + 0.0722f * p[2]) / 255f;
            }
        }
        return gray;
    }

    /// <summary>A field resampled bilinearly to another size, both covering the same picture.</summary>
    internal static float[] Resample(float[] field, int fromW, int fromH, int toW, int toH)
    {
        if (fromW == toW && fromH == toH) return field;
        var output = new float[toW * toH];
        double sx = (double)fromW / toW, sy = (double)fromH / toH;
        for (var y = 0; y < toH; y++)
        {
            var v = (y + 0.5) * sy - 0.5;
            for (var x = 0; x < toW; x++) output[y * toW + x] = Bilinear(field, fromW, fromH, (x + 0.5) * sx - 0.5, v);
        }
        return output;
    }

    private static float Bilinear(float[] field, int width, int height, double u, double v)
    {
        var x0 = (int)Math.Floor(u);
        var y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        int x1 = Math.Clamp(x0 + 1, 0, width - 1), y1 = Math.Clamp(y0 + 1, 0, height - 1);
        x0 = Math.Clamp(x0, 0, width - 1); y0 = Math.Clamp(y0, 0, height - 1);
        var top = field[y0 * width + x0] * (1 - fx) + field[y0 * width + x1] * fx;
        var bottom = field[y1 * width + x0] * (1 - fx) + field[y1 * width + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>The mean over a (2r+1)-square window at every pixel, windows clipped at the edges, through a summed-area table.</summary>
    internal static float[] Box(float[] field, int width, int height, int radius)
    {
        var sums = new double[(width + 1) * (height + 1)];
        var stride = width + 1;
        for (var y = 0; y < height; y++)
        {
            double rowSum = 0;
            for (var x = 0; x < width; x++)
            {
                rowSum += field[y * width + x];
                sums[(y + 1) * stride + x + 1] = sums[y * stride + x + 1] + rowSum;
            }
        }
        var output = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            int top = Math.Max(0, y - radius), bottom = Math.Min(height, y + radius + 1);
            for (var x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius), right = Math.Min(width, x + radius + 1);
                var total = sums[bottom * stride + right] - sums[top * stride + right] - sums[bottom * stride + left] + sums[top * stride + left];
                output[y * width + x] = (float)(total / ((right - left) * (bottom - top)));
            }
        }
        return output;
    }
}

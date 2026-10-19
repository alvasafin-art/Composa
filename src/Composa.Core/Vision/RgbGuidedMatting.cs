using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Fast guided upsampling with a joint three-channel color regression, never a luminance-only edge fit.</summary>
internal static unsafe class RgbGuidedMatting
{
    internal static SKBitmap Upsample(SKBitmap guide, SKBitmap smallGuide, SKBitmap matte, SKBitmap trimap, CancellationToken cancellation)
    {
        if (!Pixels.IsSrgb8(guide) || !Pixels.IsSrgb8(smallGuide) || matte.ColorType != SKColorType.Alpha8 || trimap.ColorType != SKColorType.Alpha8
            || smallGuide.Width != matte.Width || smallGuide.Height != matte.Height || guide.Width != trimap.Width || guide.Height != trimap.Height)
            throw new ArgumentException("RGB guided matting requires corresponding sRGB guides and Alpha8 grids.");
        var w = smallGuide.Width; var h = smallGuide.Height; var count = w * h; const int radius = 4;
        var colors = new[] { new float[count], new float[count], new float[count] }; var alpha = new float[count];
        var input = new PixelBuffer(smallGuide); var data = (byte*)matte.GetPixels(); var stride = matte.RowBytes;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        { var i = y * w + x; var p = input.Read(x, y).Straight; for (var c = 0; c < 3; c++) colors[c][i] = p[c]; alpha[i] = data[y * stride + x] / 255f; }
        float[] Mean(float[] values) { cancellation.ThrowIfCancellationRequested(); return GuidedFilter.Box(values, w, h, radius); }
        float[] Product(float[] a, float[] b)
        { var values = new float[count]; for (var i = 0; i < count; i++) values[i] = a[i] * b[i]; return Mean(values); }
        var mean = colors.Select(Mean).ToArray(); var meanAlpha = Mean(alpha);
        var covAlpha = colors.Select(c => Product(c, alpha)).ToArray();
        var covariance = new[] { Product(colors[0], colors[0]), Product(colors[0], colors[1]), Product(colors[0], colors[2]), Product(colors[1], colors[1]), Product(colors[1], colors[2]), Product(colors[2], colors[2]) };
        var coefficients = new[] { new float[count], new float[count], new float[count], new float[count] };
        for (var i = 0; i < count; i++)
        {
            const double epsilon = .0001;
            double r = mean[0][i], g = mean[1][i], b = mean[2][i];
            double rr = covariance[0][i] - r * r + epsilon, rg = covariance[1][i] - r * g, rb = covariance[2][i] - r * b;
            double gg = covariance[3][i] - g * g + epsilon, gb = covariance[4][i] - g * b, bb = covariance[5][i] - b * b + epsilon;
            var ar = gg * bb - gb * gb; var ag = rb * gb - rg * bb; var ab = rg * gb - rb * gg;
            var bg = rr * bb - rb * rb; var bc = rg * rb - rr * gb; var cc = rr * gg - rg * rg;
            var determinant = rr * ar + rg * ag + rb * ab;
            double pr = covAlpha[0][i] - r * meanAlpha[i], pg = covAlpha[1][i] - g * meanAlpha[i], pb = covAlpha[2][i] - b * meanAlpha[i];
            var cr = coefficients[0][i] = (float)((ar * pr + ag * pg + ab * pb) / determinant);
            var cg = coefficients[1][i] = (float)((ag * pr + bg * pg + bc * pb) / determinant);
            var cb = coefficients[2][i] = (float)((ab * pr + bc * pg + cc * pb) / determinant);
            coefficients[3][i] = (float)(meanAlpha[i] - cr * r - cg * g - cb * b);
        }
        var fit = coefficients.Select(Mean).ToArray();
        var result = Pixels.NewMask(guide.Width, guide.Height); var output = (byte*)result.GetPixels(); var outStride = result.RowBytes;
        var certain = (byte*)trimap.GetPixels(); var certainStride = trimap.RowBytes; var source = new PixelBuffer(guide);
        var width = source.Width; var height = source.Height; var sx = (double)w / width; var sy = (double)h / height;
        try
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
                var v = (y + .5) * sy - .5;
                for (var x = 0; x < width; x++)
                {
                    var known = certain[y * certainStride + x]; if (known != 128) { output[y * outStride + x] = known; continue; }
                    var u = (x + .5) * sx - .5; var p = source.Read(x, y).Straight;
                    var alpha = GuidedFilter.Bilinear(fit[0], w, h, u, v) * p.R + GuidedFilter.Bilinear(fit[1], w, h, u, v) * p.G
                        + GuidedFilter.Bilinear(fit[2], w, h, u, v) * p.B + GuidedFilter.Bilinear(fit[3], w, h, u, v);
                    output[y * outStride + x] = (byte)Math.Clamp((int)MathF.Round(alpha * 255), 0, 255);
                }
            });
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }
}

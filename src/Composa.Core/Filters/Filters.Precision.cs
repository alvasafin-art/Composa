using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

public static unsafe partial class ImageFilters
{
    private static (SKBitmap Result, int GrowX, int GrowY) RunHighPrecision(SKBitmap source, FilterSettings settings)
    {
        switch (settings.Kind)
        {
            case FilterKind.MotionBlur: return HighMotionBlur(source, settings.Radius, settings.Angle, settings.ClampEdges && HasOpaqueBorder(source));
            case FilterKind.Sharpen: return (HighSharpen(source, settings.Radius, settings.Amount / 50), 0, 0);
            case FilterKind.HighPass: return (HighPass(source, settings.Radius), 0, 0);
            case FilterKind.AddNoise:
                var noisy = Pixels.Clone(source);
                new AddNoiseAdjustment { Amount = settings.Amount, Gaussian = settings.Gaussian, Monochromatic = settings.Monochrome, Seed = settings.Seed }.Apply(noisy);
                return (noisy, 0, 0);
            case FilterKind.LensCorrection: return (HighLensCorrection(source, settings.Distortion / 100), 0, 0);
            case FilterKind.Vignette: return (HighVignette(source, settings), 0, 0);
            case FilterKind.BloomGlow: return HighBloom(source, settings.BloomAmount / 50, settings.BloomRadius, settings.ClampEdges && HasOpaqueBorder(source));
            case FilterKind.TonalContrast: return (HighTonalContrast(source, settings), 0, 0);
            case FilterKind.CameraRaw: return (CameraRawPixels.Apply(source, settings.CameraRaw, settings.CameraRawScale, settings.Seed), 0, 0);
            case FilterKind.RemoveBackground: return (RemoveBackground(source, settings.Amount), 0, 0);
            case FilterKind.Painterly: return (Painterly.Paint(source, settings.Painterly, settings.Seed), 0, 0);
            default: throw new NotSupportedException($"{settings.Kind} has no high precision filter implementation.");
        }
    }

    private static (SKBitmap Result, int GrowX, int GrowY) HighMotionBlur(SKBitmap source, double distance, double angle, bool clamp)
    {
        var length = Math.Max(1, (int)Math.Round(distance));
        var radians = angle * Math.PI / 180;
        double dx = Math.Cos(radians), dy = -Math.Sin(radians);
        int padX = clamp ? 0 : (int)Math.Ceiling(Math.Abs(dx) * length / 2) + 1;
        int padY = clamp ? 0 : (int)Math.Ceiling(Math.Abs(dy) * length / 2) + 1;
        var result = Pixels.NewLike(source, source.Width + padX * 2, source.Height + padY * 2);
        var input = new PixelBuffer(source); var output = new PixelBuffer(result);
        var samples = Math.Min(length, 96);
        Parallel.For(0, result.Height, y =>
        {
            for (var x = 0; x < result.Width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var i = 0; i < samples; i++)
                {
                    var t = samples == 1 ? 0 : (i / (double)(samples - 1) - .5) * length;
                    int sx = (int)Math.Round(x - padX + dx * t), sy = (int)Math.Round(y - padY + dy * t);
                    if (clamp) { sx = Math.Clamp(sx, 0, source.Width - 1); sy = Math.Clamp(sy, 0, source.Height - 1); }
                    else if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height) continue;
                    var p = input.Read(sx, sy);
                    r += p.R; g += p.G; b += p.B; a += p.A;
                }
                output.Write(x, y, new((float)(r / samples), (float)(g / samples), (float)(b / samples), (float)(a / samples)));
            }
        });
        return (result, padX, padY);
    }

    private static SKBitmap HighSharpen(SKBitmap source, double radius, double strength)
    {
        using var blurred = Pixels.Clone(source);
        FloatGaussian.ApplyInPlace(blurred, Math.Max(.3, radius / 2));
        var result = Pixels.Clone(source);
        var output = new PixelBuffer(result); var soft = new PixelBuffer(blurred);
        Parallel.For(0, source.Height, y =>
        {
            for (var x = 0; x < source.Width; x++)
            {
                var p = output.Read(x, y); var q = soft.Read(x, y);
                output.Write(x, y, new((float)(p.R + (p.R - q.R) * strength), (float)(p.G + (p.G - q.G) * strength), (float)(p.B + (p.B - q.B) * strength), p.A));
            }
        });
        return result;
    }

    private static SKBitmap HighLensCorrection(SKBitmap source, double distortion)
    {
        var result = Pixels.NewLike(source);
        var input = new PixelBuffer(source); var output = new PixelBuffer(result);
        double cx = source.Width / 2.0, cy = source.Height / 2.0, norm = Math.Sqrt(cx * cx + cy * cy);
        Parallel.For(0, source.Height, y =>
        {
            for (var x = 0; x < source.Width; x++)
            {
                double nx = (x + .5 - cx) / norm, ny = (y + .5 - cy) / norm;
                var factor = 1 + distortion * .5 * (nx * nx + ny * ny);
                double sx = cx + nx * factor * norm - .5, sy = cy + ny * factor * norm - .5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                float tx = (float)(sx - x0), ty = (float)(sy - y0);
                PixelValue At(int px, int py) => px < 0 || py < 0 || px >= source.Width || py >= source.Height ? default : input.Read(px, py);
                output.Write(x, y, (At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx) * (1 - ty) + (At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx) * ty);
            }
        });
        return result;
    }

    private static SKBitmap HighVignette(SKBitmap source, FilterSettings settings)
    {
        var result = Pixels.Clone(source);
        var frame = settings.VignetteFrame ?? new SKRect(0, 0, source.Width, source.Height);
        if (settings.VignetteAmount <= 0 || frame.Width <= 0 || frame.Height <= 0) return result;
        var output = new PixelBuffer(result);
        var color = new SKColor(settings.VignetteColor);
        Parallel.For(0, source.Height, y =>
        {
            for (var x = 0; x < source.Width; x++)
            {
                var p = output.Read(x, y); var c = p.Straight;
                if (p.A == 0 && !settings.VignetteFillsClear) continue;
                var mask = VignetteMaskAt(x + .5 - frame.Left, y + .5 - frame.Top, frame.Width, frame.Height,
                    Math.Clamp(settings.VignetteMidpoint, 0, 100), Math.Clamp(settings.VignetteRoundness, -100, 100), Math.Clamp(settings.VignetteFeather, 0, 100));
                var bright = Clamp01((Rec709(c.R, c.G, c.B) - .45) / .55);
                var amount = (float)(Clamp01(settings.VignetteAmount / 100) * mask * (1 - Math.Clamp(settings.VignetteHighlights, 0, 100) / 100 * bright));
                var tint = new PixelValue(color.Red / 255f, color.Green / 255f, color.Blue / 255f, 1);
                if (settings.VignetteFillsClear) output.Write(x, y, tint * amount + p * (1 - amount));
                else output.Write(x, y, ((c * (1 - amount) + tint * amount) with { A = c.A }).Premultiplied);
            }
        });
        return result;
    }

    private static (SKBitmap Result, int GrowX, int GrowY) HighBloom(SKBitmap source, double intensity, double radius, bool clamp)
    {
        var sigma = Math.Max(.5, radius);
        var pad = clamp ? 0 : (int)Math.Ceiling(sigma * 3);
        using var glow = Pixels.NewLike(source, source.Width + pad * 2, source.Height + pad * 2);
        var lights = new PixelBuffer(glow); var original = new PixelBuffer(source);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var p = original.Read(x, y); var c = p.Straight;
            var weight = (float)Clamp01((Rec709(c.R, c.G, c.B) - .4) / .6);
            lights.Write(x + pad, y + pad, p * weight);
        }
        FloatGaussian.ApplyInPlace(glow, sigma, clamp);
        var result = Pixels.NewLike(glow);
        using (var canvas = new SKCanvas(result)) canvas.DrawBitmap(source, pad, pad);
        var output = new PixelBuffer(result);
        var gain = (float)Math.Max(0, intensity);
        Parallel.For(0, result.Height, y =>
        {
            for (var x = 0; x < result.Width; x++)
            {
                var p = output.Read(x, y); var q = lights.Read(x, y) * gain;
                var alpha = p.A + Math.Min(1, q.A) * (1 - p.A);
                output.Write(x, y, new(p.R + q.R, p.G + q.G, p.B + q.B, alpha));
            }
        });
        return (result, pad, pad);
    }

    private static SKBitmap HighTonalContrast(SKBitmap source, FilterSettings settings)
    {
        var result = Pixels.Clone(source);
        var amount = Math.Clamp(settings.TonalAmount, 0, 100);
        double shadows = Math.Clamp(settings.TonalShadows, -100, 100), midtones = Math.Clamp(settings.TonalMidtones, -100, 100), highlights = Math.Clamp(settings.TonalHighlights, -100, 100);
        if (amount <= 0 || shadows == 0 && midtones == 0 && highlights == 0) return result;
        using var blurred = Pixels.Clone(source);
        FloatGaussian.ApplyInPlace(blurred, Math.Max(.5, settings.TonalRadius));
        var output = new PixelBuffer(result); var soft = new PixelBuffer(blurred);
        Parallel.For(0, source.Height, y =>
        {
            for (var x = 0; x < source.Width; x++)
            {
                var p = output.Read(x, y).Straight; var q = soft.Read(x, y).Straight;
                if (p.A == 0 || q.A == 0) continue;
                var lum = Rec709(p.R, p.G, p.B); var baseLum = Rec709(q.R, q.G, q.B);
                var shadowWeight = 1 - TonalSmooth(.15, .5, baseLum); var highlightWeight = TonalSmooth(.5, .85, baseLum);
                var weight = (shadows * shadowWeight + midtones * (1 - shadowWeight - highlightWeight) + highlights * highlightWeight) / 100;
                var delta = (float)(.18 * Math.Tanh((lum - baseLum) * 6) * weight * amount / 50 * (4 * lum * (1 - lum)));
                output.Write(x, y, new PixelValue(Math.Clamp(p.R + delta, 0, 1), Math.Clamp(p.G + delta, 0, 1), Math.Clamp(p.B + delta, 0, 1), p.A).Premultiplied);
            }
        });
        return result;
    }
}

using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

/// <summary>Continuous foreground/background color estimates from exact Euclidean seed fields.</summary>
internal static unsafe class MattingColors
{
    internal static SKBitmap Decontaminate(SKBitmap image, SKBitmap mask, double amount, CancellationToken cancellation)
    {
        if (amount == 0) return Pixels.Clone(image);
        var bounds = SelectionMask.Bounds(mask);
        if (bounds.IsEmpty) return Pixels.Clone(image);
        bounds.Inflate(24, 24);
        var canvas = SKRectI.Create(image.Width, image.Height);
        bounds = SKRectI.Intersect(canvas, bounds);
        if (bounds == canvas) return Recover(image, mask, amount, cancellation);
        // The nearest-colour fields need the subject and some known background, not
        // the entire photograph when the user boxed a small object.
        using var rgb = Pixels.Subset(image, bounds);
        using var coverage = Pixels.Subset(mask, bounds);
        using var patch = Recover(rgb, coverage, amount, cancellation);
        var result = Pixels.Clone(image);
        using var draw = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        draw.DrawBitmap(patch, bounds.Left, bounds.Top, paint);
        return result;
    }

    private static SKBitmap Recover(SKBitmap image, SKBitmap mask, double amount, CancellationToken cancellation)
    {
        var source = new PixelBuffer(image); var result = Pixels.Clone(image);
        var output = new PixelBuffer(result); var coverage = (byte*)mask.GetPixels(); var width = image.Width; var height = image.Height; var stride = mask.RowBytes;
        try
        {
            // An opaque edge pixel can still carry reflected or misclassified backdrop
            // colour. Use the interior as a prior, with a fallback for very thin objects.
            using var core = SelectionMask.Contract(mask, 2);
            var seeds = core != null && SelectionMask.Bounds(core, 255) is { IsEmpty: false } ? core : mask;
            var interior = (byte*)seeds.GetPixels();
            var interiorStride = seeds.RowBytes;
            var foreground = SeedField(source, interior, interiorStride, true, cancellation);
            var background = SeedField(source, coverage, stride, false, cancellation);
            using var fringe = SelectionMask.Expand(mask, 4);
            var expanded = (byte*)fringe.GetPixels(); var fringeStride = fringe.RowBytes;
            var samples = new List<PixelValue>();
            var step = Math.Max(1, Math.Max(width, height) / 500);
            for (var y = 0; y < height; y += step) for (var x = 0; x < width; x += step)
                if (coverage[y * stride + x] <= 1 && expanded[y * fringeStride + x] >= 8)
                    samples.Add(source.Read(x, y).Straight);
            var plate = Vision.ForegroundCoverage.ColourPlate(samples);
            PixelValue? Around(int[] field, int x, int y)
            {
                var sum = default(PixelValue); double weight = 0;
                for (var dy = -2; dy <= 2; dy += 2) for (var dx = -2; dx <= 2; dx += 2)
                {
                    var id = field[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)]; if (id < 0) continue;
                    var sx = id % width; var sy = id / width; var distance = (double)(sx - x) * (sx - x) + (double)(sy - y) * (sy - y);
                    var w = 1 / (1 + distance); sum += source.Read(sx, sy).Straight * (float)w; weight += w;
                }
                return weight > 0 ? sum * (float)(1 / weight) : null;
            }
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var a = coverage[(long)y * stride + x]; var color = source.Read(x, y);
                    if (a == 0 || color.A < .999f || interior[(long)y * interiorStride + x] == 255 || Around(foreground, x, y) is not { } fg) continue;
                    var alpha = a / 255f; var straight = color.Straight; var estimated = fg;
                    if (Around(background, x, y) is { } bg)
                    {
                        if (plate is { } screen)
                        {
                            // On a verified plate the inverse composite already gives
                            // the strand hue. A distant opaque sample can be skin or
                            // clothing and must not donate its hue to sparse hairs.
                            var difference = Math.Max(Math.Abs(bg.R - screen.R), Math.Max(Math.Abs(bg.G - screen.G), Math.Abs(bg.B - screen.B)));
                            var localWeight = Math.Clamp((.12f - difference) / .08f, 0, 1);
                            bg = bg * localWeight + screen * (1 - localWeight);
                            var coverageFloor = Math.Max(.10f, alpha);
                            estimated = bg + (straight - bg) * (1 / coverageFloor);
                            var residual = straight - (fg * alpha + bg * (1 - alpha));
                            var priorWeight = (1 - alpha) * Math.Clamp(1 - MathF.Sqrt(Dot(residual, residual)) / .045f, 0, 1);
                            estimated = estimated * (1 - priorWeight) + fg * priorWeight;
                        }
                        else
                        {
                            var candidate = new PixelValue((straight.R - (1 - alpha) * bg.R) / alpha,
                                (straight.G - (1 - alpha) * bg.G) / alpha, (straight.B - (1 - alpha) * bg.B) / alpha, 1);
                            var error = MathF.Sqrt(MathF.Pow(candidate.R - fg.R, 2) + MathF.Pow(candidate.G - fg.G, 2) + MathF.Pow(candidate.B - fg.B, 2));
                            // Wrong alpha or a different background texture can make unmixing explode. Such colors
                            // must fall back to the smooth foreground prior, rather than clamping into bright fringes.
                            var reliable = Math.Clamp(1 - error / .3f, 0, 1) * Math.Min(1, alpha / .2f);
                            // At high coverage the observed colour already contains real
                            // detail (for example a warm ear beside dark hair).
                            var noise = .035f + .20f * (1 - alpha);
                            var dataWeight = alpha * alpha / (alpha * alpha + noise * noise);
                            var overflow = Math.Max(0, Math.Max(Math.Max(candidate.R, Math.Max(candidate.G, candidate.B)) - 1,
                                -Math.Min(candidate.R, Math.Min(candidate.G, candidate.B))));
                            reliable = Math.Max(reliable, dataWeight / (1 + 6 * overflow));
                            estimated = a == 255 ? straight : candidate * reliable + fg * (1 - reliable);
                            // Remove only residual chroma pointing towards the local backdrop.
                            // Luminance variation along a light strand is useful texture.
                            var axis = Chroma(bg - fg);
                            var spill = Chroma(estimated - fg);
                            var power = Dot(axis, axis);
                            var projected = Dot(spill, axis);
                            if (power > .018f && projected * projected > .8f * power * Dot(spill, spill))
                                estimated -= axis * Math.Clamp(projected / power, -.85f, .85f);
                        }
                    }
                    else if (a == 255) continue;
                    estimated = new(Math.Clamp(estimated.R, 0, 1), Math.Clamp(estimated.G, 0, 1), Math.Clamp(estimated.B, 0, 1), 1);
                    var blended = straight * (float)(1 - amount) + estimated * (float)amount;
                    output.Write(x, y, (blended with { A = color.A }).Premultiplied);
                }
            });
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static float Dot(PixelValue a, PixelValue b) => a.R * b.R + a.G * b.G + a.B * b.B;

    private static PixelValue Chroma(PixelValue color)
    {
        var luma = .2126f * color.R + .7152f * color.G + .0722f * color.B;
        return new(color.R - luma, color.G - luma, color.B - luma, 0);
    }

    // A separable squared Euclidean distance transform. The map stores the seed's index, not a rounded color,
    // and has no directional ray boundaries. One int per pixel per field; per-column scratch is bounded by height.
    internal static int[] SeedField(PixelBuffer image, byte* mask, int stride, bool foreground, CancellationToken cancellation)
    {
        var width = image.Width; var height = image.Height; var result = new int[checked(width * height)];
        var options = new ParallelOptions { CancellationToken = cancellation };
        Parallel.For(0, height, options, y =>
        {
            var last = -1;
            for (var x = 0; x < width; x++)
            {
                var alpha = mask[(long)y * stride + x];
                if ((foreground ? alpha == 255 : alpha == 0) && image.Read(x, y).A >= .999f) last = x;
                result[y * width + x] = last < 0 ? -1 : y * width + last;
            }
            last = -1;
            for (var x = width - 1; x >= 0; x--)
            {
                var alpha = mask[(long)y * stride + x];
                if ((foreground ? alpha == 255 : alpha == 0) && image.Read(x, y).A >= .999f) last = x;
                var id = result[y * width + x];
                if (last >= 0 && (id < 0 || last - x < x - id % width)) result[y * width + x] = y * width + last;
            }
        });
        Parallel.For(0, width, options, () => (Labels: new int[height], F: new double[height], Sites: new int[height], Cuts: new double[height + 1]), (x, _, scratch) =>
        {
            var (labels, f, sites, cuts) = scratch; var count = -1;
            for (var y = 0; y < height; y++)
            {
                var id = labels[y] = result[y * width + x]; if (id < 0) continue;
                f[y] = Math.Pow(x - id % width, 2); double cut = double.NegativeInfinity;
                while (count >= 0)
                {
                    var previous = sites[count]; cut = (f[y] + (double)y * y - f[previous] - (double)previous * previous) / (2 * (y - previous));
                    if (cut > cuts[count]) break; count--;
                }
                count++; sites[count] = y; cuts[count] = count == 0 ? double.NegativeInfinity : cut; cuts[count + 1] = double.PositiveInfinity;
            }
            if (count >= 0)
            {
                var k = 0;
                for (var y = 0; y < height; y++) { while (cuts[k + 1] < y) k++; result[y * width + x] = labels[sites[k]]; }
            }
            return scratch;
        }, _ => { });
        return result;
    }
}

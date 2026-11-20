using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Check the predicted coverage against I = aF + (1-a)B in the original RGB grid.</summary>
internal static unsafe class ForegroundCoverage
{
    internal static void Refine(SKBitmap image, SKBitmap matte, SKRectI? prompt, int radius, CancellationToken cancellation)
    {
        var width = matte.Width; var height = matte.Height;
        var rgb = new PixelBuffer(image);
        var bounds = SKRectI.Create(width, height);
        var plate = BackgroundAlong(rgb, SKRectI.Intersect(prompt ?? SelectionMask.Bounds(matte, 128), bounds));
        var alpha = (byte*)matte.GetPixels(); var stride = matte.RowBytes;
        using var seeds = Pixels.NewMask(width, height, 128);
        var data = (byte*)seeds.GetPixels(); var seedStride = seeds.RowBytes;
        // On a coloured backdrop SAM can label a tangle of hairs and the gaps between
        // them as solid foreground. Draw seeds from the original colours in that case.
        using var support = plate.HasValue ? SelectionMask.Expand(matte, radius) : null;
        // A high-confidence pixel on the silhouette can still contain backdrop.
        // Move the colour samples one native pixel inside each certain region.
        using var inner = plate.HasValue ? null : SelectionMask.Contract(matte, 1);
        using var outer = plate.HasValue ? null : SelectionMask.Expand(matte, 1);
        var inside = inner == null ? null : (byte*)inner.GetPixels();
        var innerStride = inner?.RowBytes ?? 0;
        var dilated = outer == null ? null : (byte*)outer.GetPixels();
        var outerStride = outer?.RowBytes ?? 0;
        var cutoff = 0f;
        using var reachable = plate.HasValue ? Pixels.NewMask(width, height) : null;
        var paths = reachable == null ? null : (byte*)reachable.GetPixels();
        var pathStride = reachable?.RowBytes ?? 0;
        if (plate is { } background)
        {
            var distances = new List<float>();
            var step = Math.Max(1, Math.Max(width, height) / 100);
            for (var y = 0; y < height; y += step) for (var x = 0; x < width; x += step)
                if (alpha[y * stride + x] > 224)
                    distances.Add(Distance(rgb.Read(x, y).Straight, background));
            if (distances.Count > 0) { distances.Sort(); cutoff = Math.Max(.2f, distances[distances.Count * 4 / 5] * .8f); }
        }
        // Semi-transparent strands can be confidently labelled opaque by the
        // segmenter. On a verified colour plate, local peaks of colour separation
        // are better foreground samples than every pixel of that opaque region.
        using var separation = plate.HasValue ? Pixels.NewMask(width, height) : null;
        using var chromaRange = plate.HasValue ? Pixels.NewMask(width, height) : null;
        var dominant = plate is { } key ? key.R >= key.G && key.R >= key.B ? 0 : key.G >= key.B ? 1 : 2 : 0;
        float Excess(PixelValue p) => 1.5f * p[dominant] - .5f * (p.R + p.G + p.B);
        if (separation != null && plate is { } colour)
        {
            var levels = (byte*)separation.GetPixels(); var pitch = separation.RowBytes;
            var chroma = (byte*)chromaRange!.GetPixels(); var chromaStride = chromaRange.RowBytes;
            for (var y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                    if (alpha[y * stride + x] > 192)
                    {
                        var pixel = rgb.Read(x, y).Straight;
                        levels[y * pitch + x] = (byte)Math.Clamp(MathF.Round(255 * Distance(pixel, colour)), 0, 255);
                        chroma[y * chromaStride + x] = (byte)Math.Clamp(MathF.Round(127.5f * (Excess(colour) - Excess(pixel))), 0, 255);
                    }
            }
        }
        using var peak = separation == null ? null : SelectionMask.Expand(separation, Math.Clamp(radius, 6, 24));
        using var smoothPeak = peak == null ? null : SelectionMask.Feather(peak, Math.Max(3, radius / 2));
        var peaks = smoothPeak == null ? null : (byte*)smoothPeak.GetPixels(); var peakStride = smoothPeak?.RowBytes ?? 0;
        using var strongestChroma = chromaRange == null ? null : SelectionMask.Expand(chromaRange, Math.Clamp(radius / 2, 5, 14));
        var chromaLimits = strongestChroma == null ? null : (byte*)strongestChroma.GetPixels(); var limitStride = strongestChroma?.RowBytes ?? 0;
        for (var y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var a = alpha[y * stride + x];
                byte label = a == 0 ? (byte)0 : a == 255 ? (byte)255 : (byte)128;
                if (dilated != null && inside != null)
                    label = dilated[y * outerStride + x] == 0 ? (byte)0 : inside[y * innerStride + x] == 255 ? (byte)255 : (byte)128;
                if (plate is { } bg)
                {
                    var distance = Distance(rgb.Read(x, y).Straight, bg);
                    var localCutoff = Math.Max(cutoff, .94f * peaks[y * peakStride + x] / 255);
                    label = distance < .085f ? (byte)0 : a > 224 && distance >= localCutoff ? (byte)255 : (byte)128;
                    if (a < 240 || label != 255 || x == 0 || y == 0 || x == width - 1 || y == height - 1)
                        paths[y * pathStride + x] = 255;
                }
                data[y * seedStride + x] = label;
            }
        }
        // A coloured marking enclosed by opaque fur or clothing belongs to the object.
        // Only treat matching colours reachable from the exterior as backdrop samples.
        using var exterior = reachable == null ? null : ObjectSelection.ConnectedPiece(reachable, 0, 0, cancellation: cancellation);
        var outside = exterior == null ? null : (byte*)exterior.GetPixels();
        var outsideStride = exterior?.RowBytes ?? 0;
        if (outside != null)
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                if (outside[y * outsideStride + x] == 0 && alpha[y * stride + x] >= 240 && data[y * seedStride + x] == 0)
                    data[y * seedStride + x] = 128;
        var fg = MattingColors.SeedField(rgb, data, seedStride, true, cancellation);
        var bgSeeds = MattingColors.SeedField(rgb, data, seedStride, false, cancellation);
        var allowed = support == null ? null : (byte*)support.GetPixels(); var allowedStride = support?.RowBytes ?? 0;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var a = alpha[y * stride + x];
                if (allowed == null ? a is 0 or 255 : allowed[y * allowedStride + x] == 0) continue;
                if (outside != null && outside[y * outsideStride + x] == 0 && a >= 240 &&
                    Distance(rgb.Read(x, y).Straight, plate!.Value) < .085f) continue;
                var index = y * width + x; var f = fg[index]; var b = bgSeeds[index];
                if (f < 0 || b < 0) continue;
                if (plate.HasValue && data[y * seedStride + x] == 0)
                {
                    alpha[y * stride + x] = 0;
                    continue;
                }
                var foreground = rgb.Read(f % width, f / width).Straight;
                var background = rgb.Read(b % width, b / width).Straight;
                var observed = rgb.Read(x, y).Straight;
                if (plate is { } sampledPlate)
                {
                    var dx = x - b % width; var dy = y - b / width;
                    var local = 1 / (1f + ((float)dx * dx + (float)dy * dy) / 144);
                    background = background * local + sampledPlate * (1 - local);
                }
                var axis = foreground - background;
                var contrast = axis.R * axis.R + axis.G * axis.G + axis.B * axis.B;
                if (contrast < .035f) continue;
                var v = observed - background;
                var fitted = Math.Clamp((v.R * axis.R + v.G * axis.G + v.B * axis.B) / contrast, 0, 1);
                var error = Distance(observed, background + axis * fitted);
                var trust = Math.Clamp(1 - error / .085f, 0, 1) * .9f;
                if (plate.HasValue && data[y * seedStride + x] != 255)
                {
                    // A distant opaque patch can be a different material. Transition
                    // continuously to the local colour-separation estimate instead of
                    // painting the Voronoi cells of those sparse samples into the hair.
                    var dx = x - f % width; var dy = y - f / width;
                    var proximity = 1 / (1f + ((float)dx * dx + (float)dy * dy) / 64);
                    var localContrast = Math.Max(Math.Max(.2f, cutoff), peaks[y * peakStride + x] / 255f);
                    var localAlpha = Math.Clamp(Distance(observed, background) / localContrast, 0, 1);
                    var fitWeight = proximity * Math.Clamp((.12f - error) / .08f, 0, 1);
                    fitted = fitted * fitWeight + localAlpha * (1 - fitWeight);
                    // Unmixing against this background must yield displayable RGB.
                    // This bound is especially important for dark gaps and saturated
                    // clothing, where an underestimated alpha produces magenta fringes.
                    static float MinimumAlpha(float pixel, float back) => pixel > back
                        ? (pixel - back) / Math.Max(.001f, 1 - back) : (back - pixel) / Math.Max(.001f, back);
                    fitted = Math.Max(fitted, Math.Max(MinimumAlpha(observed.R, background.R),
                        Math.Max(MinimumAlpha(observed.G, background.G), MinimumAlpha(observed.B, background.B))));
                    // Removing a green plate must not introduce magenta that is absent
                    // from nearby foreground. Constrain the inverse mix along that hue.
                    var chromaSpan = Math.Max(Excess(background) + .025f, chromaLimits[y * limitStride + x] / 127.5f + .025f);
                    fitted = Math.Max(fitted, Math.Clamp((Excess(background) - Excess(observed)) / Math.Max(.05f, chromaSpan), 0, 1));
                    trust = 1;
                }
                else if (!plate.HasValue)
                {
                    trust *= MattingColors.SampleConfidence(x, y, f, b, width, foreground, background);
                    var prior = a / 255f;
                    var uncertainty = prior * (1 - prior);
                    fitted = Math.Clamp(fitted, prior - uncertainty, prior + uncertainty);
                }
                // Grow coverage only where the fitted colour agrees with both local samples.
                if (a == 0 && (error > .035f || fitted > .8f)) continue;
                alpha[y * stride + x] = (byte)Math.Clamp(MathF.Round(a + trust * (255 * fitted - a)), 0, 255);
            }
        });
        Pixels.Invalidate(matte);
    }

    private static float Distance(PixelValue a, PixelValue b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    internal static PixelValue? BackgroundAlong(PixelBuffer rgb, SKRectI box)
    {
        if (box.Width < 8 || box.Height < 8) return null;
        var samples = new List<PixelValue>(128);
        for (var i = 0; i < 32; i++)
        {
            var x = box.Left + (box.Width - 1) * i / 31; var y = box.Top + (box.Height - 1) * i / 31;
            samples.Add(rgb.Read(x, box.Top).Straight); samples.Add(rgb.Read(x, box.Bottom - 1).Straight);
            samples.Add(rgb.Read(box.Left, y).Straight); samples.Add(rgb.Read(box.Right - 1, y).Straight);
        }
        return ColourPlate(samples);
    }

    internal static PixelValue? ColourPlate(List<PixelValue> samples, bool allowUniformNeutral = false)
    {
        if (samples.Count < 16) return null;
        float Median(Func<PixelValue, float> channel) => samples.Select(channel).Order().ElementAt(samples.Count / 2);
        var bg = new PixelValue(Median(p => p.R), Median(p => p.G), Median(p => p.B), 1);
        if (Math.Max(bg.R, Math.Max(bg.G, bg.B)) - Math.Min(bg.R, Math.Min(bg.G, bg.B)) < .18f)
        {
            // A truly uniform neutral studio backdrop is useful for inverse compositing,
            // but greys in an ordinary photo must not act as a chroma key.
            return allowUniformNeutral && samples.Count(p => p.A >= .99f && Distance(p, bg) < .015f) >= samples.Count * .95f ? bg : null;
        }
        return samples.Count(p => p.A >= .99f && Distance(p, bg) < .085f) >= samples.Count * .70f ? bg : null;
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Measures lighting between corresponding patches, then applies it to the original pixels.</summary>
internal sealed class SceneMatchedLighting
{
    private const int AnalysisSize = 352;
    private const int PatchRadius = 6;
    private const int PatchWidth = 2 * PatchRadius + 1;
    private const int PatchPixels = PatchWidth * PatchWidth;
    private const int SearchRadius = 5;
    private const int SearchWidth = 2 * SearchRadius + 1;
    private const int Border = PatchRadius + 2 * SearchRadius;
    private const float LuminanceFloor = .008f;
    private static readonly Vector3 Luminance = new(.2126f, .7152f, .0722f);

    private readonly SKRectI area;
    private readonly int width, height;
    private readonly Vector4[] corrections;

    private SceneMatchedLighting(SKRectI area, int width, int height, Vector4[] corrections)
    {
        this.area = area;
        this.width = width;
        this.height = height;
        this.corrections = corrections;
    }

    internal static SceneMatchedLighting? TryCreate(SceneEditInput input, SKBitmap baseline, SKBitmap generated,
        CancellationToken token)
    {
        var bounds = input.SubjectBounds;
        const int padding = Border + PatchRadius;
        var area = SKRectI.Intersect(new(bounds.Left - padding, bounds.Top - padding,
            bounds.Right + padding, bounds.Bottom + padding), baseline.Info.Rect);
        var scale = Math.Min(1, AnalysisSize / (double)Math.Max(area.Width, area.Height));
        var width = Math.Max(1, (int)Math.Round(area.Width * scale));
        var height = Math.Max(1, (int)Math.Round(area.Height * scale));
        if (width <= 2 * Border || height <= 2 * Border) return null;

        var source = new Vector3[width * height];
        var target = new Vector3[source.Length];
        var foreground = new Vector3[source.Length];
        var coverage = new byte[source.Length];
        var core = new bool[source.Length];
        var opaquePair = new bool[source.Length];
        var count = scale < 1 ? 2 : 1;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                var alpha = 0;
                var selected = true;
                opaquePair[p] = true;
                for (var sy = 0; sy < count; sy++)
                    for (var sx = 0; sx < count; sx++)
                    {
                        var ix = area.Left + (int)((x + (sx + .5) / count) * area.Width / width);
                        var iy = area.Top + (int)((y + (sy + .5) / count) * area.Height / height);
                        var reference = baseline.GetPixel(ix, iy);
                        var model = generated.GetPixel(ix, iy);
                        var subjectAlpha = input.ObjectAlpha.GetPixel(ix, iy).Alpha;
                        source[p] += SceneLightingMap.LinearRgb(reference);
                        target[p] += SceneLightingMap.LinearRgb(model);
                        foreground[p] += SceneLightingMap.LinearRgb(input.ModelForeground.GetPixel(ix, iy)) * subjectAlpha;
                        alpha += subjectAlpha;
                        selected &= input.SubjectMask.GetPixel(ix, iy).Alpha > 0;
                        opaquePair[p] &= reference.Alpha >= 237 && model.Alpha >= 237;
                    }
                source[p] /= count * count;
                target[p] /= count * count;
                foreground[p] /= Math.Max(1, alpha);
                coverage[p] = (byte)(alpha / (count * count));
                core[p] = coverage[p] >= 237 && selected;
                opaquePair[p] &= core[p];
            }
        }

        var sourceImage = new PatchImage(source, core, width, height, token);
        var targetImage = new PatchImage(target, opaquePair, width, height, token);
        var samples = new List<Sample>();
        for (var y = Border; y < height - Border; y += PatchRadius)
        {
            token.ThrowIfCancellationRequested();
            for (var x = Border; x < width - Border; x += PatchRadius)
            {
                if (!sourceImage.Interior[y * width + x]) continue;
                var forward = FindMatch(sourceImage, targetImage, x, y);
                if (forward == null) continue;
                var match = forward.Value;
                var reverse = FindMatch(targetImage, sourceImage, match.X, match.Y);
                if (reverse is not { } back || Math.Abs(back.X - x) > 1 || Math.Abs(back.Y - y) > 1) continue;
                var confidence = (Math.Min(match.Score, back.Score) - .3f) / .7f *
                    Math.Min(1, Math.Min(match.Margin, back.Margin) / .12f);
                if (Measure(sourceImage, targetImage, x, y, match.X, match.Y, confidence) is { } sample)
                    samples.Add(sample);
            }
        }

        // A flat or tiny subject may have no texture to register. Retain the existing
        // material fit when there is too little evidence to construct a matched field.
        if (samples.Count < 4) return null;
        var weight = samples.Sum(sample => sample.Confidence);
        var accumulated = 0f;
        var exposure = 0f;
        foreach (var sample in samples.OrderBy(sample => sample.Exposure))
        {
            exposure = sample.Exposure;
            accumulated += sample.Confidence;
            if (accumulated >= weight / 2) break;
        }

        var material = MaterialGuide(foreground, coverage, width, height, token);
        var corrections = Interpolate(samples, material, width, height, exposure, token);
        return new(area, width, height, corrections);
    }

    internal Vector3 Apply(int x, int y, Vector3 source, bool fitSrgb)
    {
        if (x < area.Left || x >= area.Right || y < area.Top || y >= area.Bottom) return source;
        var light = Vector3.Dot(source, Luminance);
        if (light <= 1e-6f) return source;
        var sx = (x + .5f - area.Left) * width / area.Width - .5f;
        var sy = (y + .5f - area.Top) * height / area.Height - .5f;
        var left = (int)MathF.Floor(sx);
        var top = (int)MathF.Floor(sy);
        Vector4 At(int col, int row) => corrections[Math.Clamp(row, 0, height - 1) * width + Math.Clamp(col, 0, width - 1)];
        var adjustment = Vector4.Lerp(Vector4.Lerp(At(left, top), At(left + 1, top), sx - left),
            Vector4.Lerp(At(left, top + 1), At(left + 1, top + 1), sx - left), sy - top);
        if (adjustment == Vector4.Zero) return source;

        var desired = Math.Clamp((light + LuminanceFloor) * MathF.Exp(adjustment.W) - LuminanceFloor, 0, 1);
        var tinted = source * new Vector3(Tint(adjustment.X), Tint(adjustment.Y), Tint(adjustment.Z));
        tinted *= desired / Math.Max(1e-6f, Vector3.Dot(tinted, Luminance));
        var peak = Math.Max(tinted.X, Math.Max(tinted.Y, tinted.Z));
        if (fitSrgb && peak > 1)
        {
            // Compress saturated highlights toward their luminance to keep the hue.
            var neutral = new Vector3(desired);
            tinted = neutral + (tinted - neutral) * ((1 - desired) / (peak - desired));
        }
        return tinted;

        static float Tint(float value) => MathF.Exp(Math.Clamp(value, -.6f, .6f));
    }

    private readonly record struct Match(int X, int Y, float Score, float Margin);

    private static Match? FindMatch(PatchImage source, PatchImage target, int x, int y)
    {
        var center = y * source.Width + x;
        var norm = source.Norm[center];
        const float minimumContrast = .019f;
        if (norm < minimumContrast * PatchWidth || !HasSubjectTexture(source, x, y, minimumContrast)) return null;

        Span<float> kernel = stackalloc float[PatchPixels];
        var mean = source.Mean[center];
        for (var row = 0; row < PatchWidth; row++)
            for (var col = 0; col < PatchWidth; col++)
                kernel[row * PatchWidth + col] = source.Detail[(y - PatchRadius + row) * source.Width + x - PatchRadius + col] - mean;

        Span<float> scores = stackalloc float[SearchWidth * SearchWidth];
        scores.Fill(-1);
        var best = .42f;
        var bestX = 0;
        var bestY = 0;
        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
            for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                var candidate = (y + dy) * target.Width + x + dx;
                if (!target.Interior[candidate] || target.Norm[candidate] <= 1e-6f) continue;
                var dot = DotPatch(kernel, target.Detail, candidate - PatchRadius * (target.Width + 1), target.Width);
                var score = Math.Clamp(dot / (norm * target.Norm[candidate]), -1, 1);
                scores[(dy + SearchRadius) * SearchWidth + dx + SearchRadius] = score;
                if (score <= best) continue;
                best = score;
                bestX = dx;
                bestY = dy;
            }
        var runnerUp = -1f;
        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
            for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
                if (Math.Abs(dx - bestX) > 1 || Math.Abs(dy - bestY) > 1)
                    runnerUp = Math.Max(runnerUp, scores[(dy + SearchRadius) * SearchWidth + dx + SearchRadius]);
        var margin = best - runnerUp;
        return best > .42f && margin >= .022f ? new Match(x + bestX, y + bestY, best, margin) : null;
    }

    private static bool HasSubjectTexture(PatchImage source, int x, int y, float minimumContrast)
    {
        // The high-pass filter also sees the background at the silhouette. Require
        // contrast within the subject before treating that response as texture.
        var count = 0;
        double sum = 0, squares = 0;
        for (var row = y - PatchRadius; row <= y + PatchRadius; row++)
            for (var col = x - PatchRadius; col <= x + PatchRadius; col++)
            {
                var p = row * source.Width + col;
                if (!source.Core[p]) continue;
                var light = (double)source.Light[p];
                sum += light;
                squares += light * light;
                count++;
            }

        var mean = sum / count;
        var variance = squares / count - mean * mean;
        var threshold = (mean + LuminanceFloor) * minimumContrast;
        return variance >= threshold * threshold;
    }

    private static float DotPatch(ReadOnlySpan<float> kernel, float[] detail, int start, int stride)
    {
        ref var weights = ref MemoryMarshal.GetReference(kernel);
        ref var pixels = ref MemoryMarshal.GetArrayDataReference(detail);
        var sum = Vector128<float>.Zero;
        var tail = 0f;
        // A 13-pixel row has three four-wide vectors and one scalar.
        for (var row = 0; row < PatchWidth; row++)
        {
            var k = row * PatchWidth;
            var p = start + row * stride;
            for (var col = 0; col < PatchWidth - 1; col += 4)
                sum += Vector128.LoadUnsafe(ref weights, (nuint)(k + col)) *
                    Vector128.LoadUnsafe(ref pixels, (nuint)(p + col));
            tail += kernel[k + PatchWidth - 1] * detail[p + PatchWidth - 1];
        }
        return Vector128.Sum(sum) + tail;
    }

    private readonly record struct Sample(int X, int Y, float Exposure, float Confidence, Vector3 Tint,
        float TintConfidence, Vector3 Material);

    private static Sample? Measure(PatchImage source, PatchImage target, int x, int y, int tx, int ty, float confidence)
    {
        Span<float> exposure = stackalloc float[PatchPixels];
        Span<float> reds = stackalloc float[PatchPixels];
        Span<float> greens = stackalloc float[PatchPixels];
        Span<float> blues = stackalloc float[PatchPixels];
        Span<Vector3> tints = stackalloc Vector3[PatchPixels];
        var material = Chromaticity(source.Rgb[y * source.Width + x]);
        var count = 0;
        var colorCount = 0;
        for (var dy = -PatchRadius; dy <= PatchRadius; dy++)
            for (var dx = -PatchRadius; dx <= PatchRadius; dx++)
            {
                var p = (y + dy) * source.Width + x + dx;
                var q = (ty + dy) * target.Width + tx + dx;
                var before = source.Light[p];
                var after = target.Light[q];
                if (!source.Core[p] || !target.Core[q] || before <= .02f || after <= .02f) continue;
                exposure[count++] = MathF.Log((after + LuminanceFloor) / (before + LuminanceFloor));
                var original = source.Rgb[p];
                var edited = target.Rgb[q];
                if (!ColorReliable(original) || !ColorReliable(edited) ||
                    Vector3.DistanceSquared(Chromaticity(original), material) >= .11f * .11f) continue;
                var change = edited / after / (original / before);
                reds[colorCount] = MathF.Log(change.X);
                greens[colorCount] = MathF.Log(change.Y);
                blues[colorCount] = MathF.Log(change.Z);
                tints[colorCount] = new(reds[colorCount], greens[colorCount], blues[colorCount]);
                colorCount++;
            }
        if (count < 55) return null;

        var levels = exposure[..count];
        var gain = Median(levels);
        for (var i = 0; i < levels.Length; i++) levels[i] = Math.Abs(levels[i] - gain);
        confidence /= 1 + 3 * Median(levels);
        if (colorCount < 25) return new(x, y, gain, confidence, Vector3.Zero, 0, material);

        var tint = new Vector3(Median(reds[..colorCount]), Median(greens[..colorCount]), Median(blues[..colorCount]));
        for (var i = 0; i < colorCount; i++) exposure[i] = Vector3.Distance(tints[i], tint);
        var tintConfidence = confidence * Math.Min(1, colorCount / 70f) * MathF.Exp(-4 * Median(exposure[..colorCount]));
        return new(x, y, gain, confidence, tint, tintConfidence, material);

        static bool ColorReliable(Vector3 rgb) =>
            rgb.X is > .008f and < .97f && rgb.Y is > .008f and < .97f && rgb.Z is > .008f and < .97f;
    }

    private static float Median(Span<float> sample)
    {
        sample.Sort();
        return (sample[sample.Length / 2] + sample[(sample.Length - 1) / 2]) * .5f;
    }

    private static Vector3 Chromaticity(Vector3 rgb) => rgb / (.0001f + rgb.X + rgb.Y + rgb.Z);

    private static Vector3[] MaterialGuide(Vector3[] foreground, byte[] alpha, int width, int height, CancellationToken token)
    {
        const int radius = 4;
        var material = new Vector3[foreground.Length];
        var kernel = GaussianKernel(1.4f, radius);
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var sum = Vector3.Zero;
                for (var dy = -radius; dy <= radius; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var p = Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1);
                        sum += foreground[p] * (alpha[p] / 255f * kernel[dy + radius] * kernel[dx + radius]);
                    }
                material[y * width + x] = Chromaticity(sum);
            }
        }
        return material;
    }

    private struct Correction
    {
        public float Exposure, ExposureWeight, TintWeight, SquaredWeight;
        public Vector3 Tint, SquaredTint;
    }

    private static Vector4[] Interpolate(List<Sample> samples, Vector3[] material, int width, int height,
        float exposure, CancellationToken token)
    {
        const int radius = 32;
        var kernel = GaussianKernel(12, radius, normalize: false);
        var field = new Correction[material.Length];
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested();
            var residual = Math.Clamp(sample.Exposure - exposure, -.95f, .95f);
            for (var y = Math.Max(0, sample.Y - radius); y <= Math.Min(height - 1, sample.Y + radius); y++)
                for (var x = Math.Max(0, sample.X - radius); x <= Math.Min(width - 1, sample.X + radius); x++)
                {
                    var p = y * width + x;
                    var spatial = kernel[y - sample.Y + radius] * kernel[x - sample.X + radius];
                    var distance = Vector3.DistanceSquared(material[p], sample.Material);
                    var lightWeight = spatial * sample.Confidence * (.25f + .75f * MathF.Exp(-distance / (2 * .13f * .13f)));
                    var tintWeight = spatial * sample.TintConfidence * MathF.Exp(-distance / (2 * .076f * .076f));
                    ref var cell = ref field[p];
                    cell.Exposure += lightWeight * residual;
                    cell.ExposureWeight += lightWeight;
                    cell.Tint += tintWeight * sample.Tint;
                    cell.SquaredTint += tintWeight * sample.Tint * sample.Tint;
                    cell.TintWeight += tintWeight;
                    cell.SquaredWeight += tintWeight * tintWeight;
                }
        }

        var result = new Vector4[field.Length];
        for (var p = 0; p < field.Length; p++)
        {
            ref readonly var cell = ref field[p];
            var total = Math.Max(1e-6f, cell.TintWeight);
            var mean = cell.Tint / total;
            var variance = Vector3.Max(Vector3.Zero, cell.SquaredTint / total - mean * mean);
            var agreement = MathF.Exp(-4 * MathF.Sqrt(variance.X + variance.Y + variance.Z));
            var effective = cell.TintWeight * cell.TintWeight / Math.Max(1e-6f, cell.SquaredWeight);
            var support = Math.Clamp((effective - 1.4f) / 2.9f, 0, 1) * cell.TintWeight / (.23f + cell.TintWeight);
            result[p] = new(mean * (support * agreement), exposure + cell.Exposure / (.24f + cell.ExposureWeight));
        }
        return result;
    }

    private static float[] GaussianKernel(float sigma, int radius, bool normalize = true)
    {
        var kernel = new float[2 * radius + 1];
        for (var i = -radius; i <= radius; i++)
            kernel[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma));
        if (!normalize) return kernel;
        var total = kernel.Sum();
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= total;
        return kernel;
    }

    private sealed class PatchImage
    {
        internal readonly Vector3[] Rgb;
        internal readonly bool[] Core, Interior;
        internal readonly float[] Light, Detail, Mean, Norm;
        internal readonly int Width;

        internal PatchImage(Vector3[] rgb, bool[] core, int width, int height, CancellationToken token)
        {
            Rgb = rgb;
            Core = core;
            Width = width;
            Light = new float[rgb.Length];
            var log = new float[rgb.Length];
            for (var p = 0; p < rgb.Length; p++)
            {
                Light[p] = Vector3.Dot(rgb[p], Luminance);
                log[p] = MathF.Log(Light[p] + LuminanceFloor);
            }
            var smooth = Blur(log, width, height, .7f, token);
            Detail = Blur(smooth, width, height, 2.4f, token);
            for (var p = 0; p < smooth.Length; p++) Detail[p] = smooth[p] - Detail[p];

            var stride = width + 1;
            var integral = new double[stride * (height + 1)];
            var squares = new double[integral.Length];
            var coverage = new int[integral.Length];
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                double sum = 0, energy = 0;
                var covered = 0;
                for (var x = 0; x < width; x++)
                {
                    var p = y * width + x;
                    var q = (y + 1) * stride + x + 1;
                    sum += Detail[p];
                    energy += Detail[p] * Detail[p];
                    covered += core[p] ? 1 : 0;
                    integral[q] = integral[q - stride] + sum;
                    squares[q] = squares[q - stride] + energy;
                    coverage[q] = coverage[q - stride] + covered;
                }
            }

            Mean = new float[rgb.Length];
            Norm = new float[rgb.Length];
            Interior = new bool[rgb.Length];
            const int guard = 3;
            for (var y = PatchRadius; y < height - PatchRadius; y++)
                for (var x = PatchRadius; x < width - PatchRadius; x++)
                {
                    var p = y * width + x;
                    if (Area(coverage, x, y, guard) != (2 * guard + 1) * (2 * guard + 1)) continue;
                    var sum = Area(integral, x, y, PatchRadius);
                    var energy = Area(squares, x, y, PatchRadius);
                    Interior[p] = true;
                    Mean[p] = (float)(sum / PatchPixels);
                    Norm[p] = (float)Math.Sqrt(Math.Max(0, energy - sum * sum / PatchPixels));
                }

            T Area<T>(T[] table, int x, int y, int radius) where T : INumber<T>
            {
                var top = (y - radius) * stride;
                var bottom = (y + radius + 1) * stride;
                var left = x - radius;
                var right = x + radius + 1;
                return table[bottom + right] - table[bottom + left] - table[top + right] + table[top + left];
            }
        }

        private static float[] Blur(float[] values, int width, int height, float sigma, CancellationToken token)
        {
            var radius = (int)MathF.Ceiling(4 * sigma);
            var kernel = GaussianKernel(sigma, radius);
            var horizontal = new float[values.Length];
            var vertical = new float[values.Length];
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                    for (var dx = -radius; dx <= radius; dx++)
                        horizontal[y * width + x] += values[y * width + Math.Clamp(x + dx, 0, width - 1)] * kernel[dx + radius];
            }
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                    for (var dy = -radius; dy <= radius; dy++)
                        vertical[y * width + x] += horizontal[Math.Clamp(y + dy, 0, height - 1) * width + x] * kernel[dy + radius];
            }
            return vertical;
        }
    }
}

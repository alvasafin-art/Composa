using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>ViTMatte's pinned RGB + trimap tensor contract. Model pixels are sRGB8; document colors are untouched.</summary>
public static unsafe class TrimapMatting
{
    public const int WorkSide = 1024;
    private static readonly SemaphoreSlim inferenceGate = new(1, 1);
    public static bool IsAvailable => ModelRunner.CanRun(RefinementModels.VitMatte);

    public static SKBitmap Refine(SKBitmap image, SKBitmap mask, int radius = 8, SKBitmap? marks = null, CancellationToken cancellation = default,
        bool fineDetail = false, IProgress<(int Done, int Total)>? progress = null)
    {
        using var trimap = AlphaMatting.Trimap(mask, radius, marks);
        if (mask.Width != image.Width || mask.Height != image.Height) throw new ArgumentException("Matting requires matching image and mask grids.");
        if (image.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) throw new ArgumentException("Matting requires a working RGB image.");
        cancellation.ThrowIfCancellationRequested();
        if (trimap.GetPixelSpan().IndexOf((byte)128) < 0) return Pixels.Clone(trimap);
        var scale = Math.Min(1, (double)WorkSide / Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale)); var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var small = Pixels.NewColor(width, height);
        using (var canvas = new SKCanvas(small)) canvas.DrawImage(Pixels.ImageOf(image), new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var reduced = ReduceTrimap(trimap, width, height, cancellation);
        using var coarse = Infer(small, reduced, cancellation);
        if (scale == 1) return Pixels.Clone(coarse);
        // This is an owned model-boundary guide, never substituted for the document's high-precision pixels.
        using var displayGuide = Pixels.IsSrgb8(image) ? null : Pixels.DisplayCopy(image);
        var fullGuide = displayGuide ?? image;
        var result = RgbGuidedMatting.Upsample(fullGuide, small, coarse, trimap, cancellation);
        try { if (fineDetail) RefineDetails(fullGuide, trimap, result, cancellation, progress); return result; }
        catch { result.Dispose(); throw; }
    }

    private static SKBitmap Infer(SKBitmap image, SKBitmap trimap, CancellationToken cancellation)
    {
        var (input, paddedWidth, paddedHeight) = Pack(image, trimap);
        var (output, outputWidth, outputHeight) = Run(input, paddedWidth, paddedHeight, cancellation);
        if (outputWidth != paddedWidth || outputHeight != paddedHeight || output.Length != outputWidth * outputHeight)
            throw new InvalidDataException("AI Matting returned an unexpected output grid.");
        var width = image.Width; var height = image.Height; var coarse = Pixels.NewMask(width, height);
        var dst = (byte*)coarse.GetPixels(); var stride = coarse.RowBytes; var certain = (byte*)trimap.GetPixels(); var certainStride = trimap.RowBytes;
        try
        {
            for (var y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var value = output[y * outputWidth + x]; if (!float.IsFinite(value)) throw new InvalidDataException("AI Matting returned an invalid alpha value.");
                    var known = certain[y * certainStride + x];
                    dst[y * stride + x] = known == 128 ? (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255) : known;
                }
            }
            return coarse;
        }
        catch { coarse.Dispose(); throw; }
    }

    private static void RefineDetails(SKBitmap image, SKBitmap trimap, SKBitmap result, CancellationToken cancellation, IProgress<(int Done, int Total)>? progress)
    {
        const int step = 512, context = 256, overlap = 64;
        var width = image.Width; var height = image.Height; var original = (byte*)trimap.GetPixels(); var stride = trimap.RowBytes;
        var guide = (byte*)result.GetPixels(); var guideStride = result.RowBytes;
        var tiles = new List<(SKRectI Core, SKRectI Output)>();
        for (var top = 0; top < height; top += step) for (var left = 0; left < width; left += step)
        {
            cancellation.ThrowIfCancellationRequested();
            var core = new SKRectI(left, top, Math.Min(width, left + step), Math.Min(height, top + step));
            var area = new SKRectI(Math.Max(0, left - overlap), Math.Max(0, top - overlap), Math.Min(width, core.Right + overlap), Math.Min(height, core.Bottom + overlap));
            var needed = false;
            for (var y = area.Top; y < area.Bottom && !needed; y++) for (var x = area.Left; x < area.Right; x++) if (original[y * stride + x] == 128) { needed = true; break; }
            if (needed) tiles.Add((core, area));
        }
        if (tiles.Count == 0) return;
        // At most four overlapping predictions, each with weight <= 63: sums fit in ushort and weights in byte.
        var sum = new ushort[checked(width * height)]; var weights = new byte[sum.Length]; var done = 0;
        foreach (var (core, output) in tiles)
        {
            cancellation.ThrowIfCancellationRequested(); progress?.Report((done, tiles.Count));
            var area = new SKRectI(Math.Max(0, core.Left - context), Math.Max(0, core.Top - context), Math.Min(width, core.Right + context), Math.Min(height, core.Bottom + context));
            using var crop = Pixels.NewColor(area.Width, area.Height); using var local = Pixels.NewMask(area.Width, area.Height);
            using (var canvas = new SKCanvas(crop)) canvas.DrawBitmap(image, -area.Left, -area.Top);
            var data = (byte*)local.GetPixels(); var localStride = local.RowBytes;
            for (var y = 0; y < area.Height; y++) for (var x = 0; x < area.Width; x++)
            {
                var xx = x + area.Left; var yy = y + area.Top; var a = original[yy * stride + xx];
                // Coarse certainty supplies context outside the part we write, never replacing a user's constraint.
                if (a == 128 && !output.Contains(xx, yy)) { var prior = guide[yy * guideStride + xx]; if (prior <= 2) a = 0; else if (prior >= 253) a = 255; }
                data[y * localStride + x] = a;
            }
            using var predicted = Infer(crop, local, cancellation); var values = (byte*)predicted.GetPixels(); var predictedStride = predicted.RowBytes;
            static float Axis(int value, int start, int end, int limit)
            {
                var a = start == 0 ? 1 : Math.Clamp((value + .5f - (start - overlap)) / (2 * overlap), 0, 1);
                var b = end == limit ? 1 : Math.Clamp((end + overlap - (value + .5f)) / (2 * overlap), 0, 1); return Math.Min(a, b);
            }
            for (var y = output.Top; y < output.Bottom; y++) for (var x = output.Left; x < output.Right; x++)
            {
                if (original[y * stride + x] != 128) continue;
                var weight = (int)MathF.Round(Axis(x, core.Left, core.Right, width) * Axis(y, core.Top, core.Bottom, height) * 63); if (weight == 0) continue;
                var i = y * width + x;
                sum[i] += (ushort)(values[(y - area.Top) * predictedStride + x - area.Left] * weight); weights[i] += (byte)weight;
            }
            done++; progress?.Report((done, tiles.Count));
        }
        for (var y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++) if (weights[y * width + x] > 0) guide[y * guideStride + x] = (byte)Math.Clamp((int)MathF.Round(sum[y * width + x] / (float)weights[y * width + x]), 0, 255);
        }
        Pixels.Invalidate(result);
    }

    private static (float[], int, int) Run(float[] input, int width, int height, CancellationToken cancellation)
    {
        inferenceGate.Wait(cancellation);
        try { return ModelRunner.RunImage(RefinementModels.VitMatte, input, width, height, cancellation); }
        finally { inferenceGate.Release(); }
    }

    internal static (float[] Input, int Width, int Height) Pack(SKBitmap srgb, SKBitmap trimap)
    {
        if (!Pixels.IsSrgb8(srgb) || trimap.ColorType != SKColorType.Alpha8 || srgb.Width != trimap.Width || srgb.Height != trimap.Height)
            throw new ArgumentException("AI Matting packing requires matching sRGB8 and Alpha8 inputs.");
        var width = (srgb.Width + 31) / 32 * 32; var height = (srgb.Height + 31) / 32 * 32; var area = width * height;
        var result = new float[area * 4]; var pixels = new PixelBuffer(srgb); var mask = (byte*)trimap.GetPixels(); var stride = trimap.RowBytes;
        for (var y = 0; y < pixels.Height; y++) for (var x = 0; x < pixels.Width; x++)
        {
            var p = pixels.Read(x, y).Straight; var i = y * width + x;
            result[i] = p.R * 2 - 1; result[area + i] = p.G * 2 - 1; result[2 * area + i] = p.B * 2 - 1;
            result[3 * area + i] = mask[(long)y * stride + x] / 255f;
        }
        // Padding is zero AFTER normalization, on the right and bottom, as the pinned processor specifies.
        return (result, width, height);
    }

    private static SKBitmap ReduceTrimap(SKBitmap trimap, int width, int height, CancellationToken cancellation)
    {
        if (trimap.Width == width && trimap.Height == height) return Pixels.Clone(trimap);
        var result = Pixels.NewMask(width, height); var input = (byte*)trimap.GetPixels(); var output = (byte*)result.GetPixels();
        var inStride = trimap.RowBytes; var outStride = result.RowBytes; var sourceWidth = trimap.Width; var sourceHeight = trimap.Height;
        try
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
                int top = (int)Math.Floor((double)y * sourceHeight / height), bottom = Math.Min(sourceHeight, (int)Math.Ceiling((double)(y + 1) * sourceHeight / height));
                for (var x = 0; x < width; x++)
                {
                    int left = (int)Math.Floor((double)x * sourceWidth / width), right = Math.Min(sourceWidth, (int)Math.Ceiling((double)(x + 1) * sourceWidth / width));
                    byte low = 255, high = 0;
                    for (var yy = top; yy < bottom; yy++) for (var xx = left; xx < right; xx++) { var a = input[yy * inStride + xx]; low = Math.Min(low, a); high = Math.Max(high, a); }
                    output[y * outStride + x] = low == high ? low : (byte)128;
                }
            });
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}

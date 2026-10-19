using Composa.Rendering;
using Composa.Model;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Selections;

public sealed record MaskRefinementSettings(double Smooth = 0, double Feather = 0, double Contrast = 0, int Shift = 0, bool EdgeAware = true, int Radius = 8);

public static unsafe class MaskRefinement
{
    /// <summary>An owned full-canvas RGB source, before the active raster's old visibility mask and layer styles.</summary>
    public static SKBitmap Source(Document document, bool allLayers = false)
    {
        if (allLayers || document.ActiveLayer is not { Pixels: not null } active) return DocumentRenderer.Flatten(document);
        var layer = active.Clone(); layer.Mask = null; layer.VectorMask = null; layer.Effects = null;
        layer.Visible = true; layer.Opacity = layer.FillOpacity = 1; layer.Blend = BlendMode.Normal; layer.BlendIf = new(); layer.Clipped = false;
        return DocumentRenderer.RenderLayers(document, [layer], new(0, 0, document.Width, document.Height));
    }

    public static SKBitmap Refine(SKBitmap mask, SKBitmap image, MaskRefinementSettings settings, CancellationToken cancellation = default)
    {
        ValidateGrids(image, mask);
        if (!double.IsFinite(settings.Smooth) || !double.IsFinite(settings.Feather) || !double.IsFinite(settings.Contrast)) throw new ArgumentException("Refinement parameters must be finite.");
        settings = settings with { Shift = Math.Clamp(settings.Shift, -100, 100), Radius = Math.Clamp(settings.Radius, 0, 100) };
        cancellation.ThrowIfCancellationRequested();
        var working = Pixels.Clone(mask);
        try
        {
        if (settings.Smooth > 0)
        {
            var next = SelectionMask.Feather(working, (float)Math.Clamp(settings.Smooth, 0, 20)); working.Dispose(); working = next;
        }
        if (settings.EdgeAware && settings.Radius > 0)
        {
            // Fit on the existing fast guided-filter working grid; no model or full-frame float pyramid needed.
            var scale = Math.Min(1, 512.0 / Math.Max(mask.Width, mask.Height));
            var w = Math.Max(1, (int)Math.Round(mask.Width * scale)); var h = Math.Max(1, (int)Math.Round(mask.Height * scale));
            var coarse = new float[w * h]; var input = (byte*)working.GetPixels();
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
                coarse[y * w + x] = input[(long)Math.Min(mask.Height - 1, (int)(y / scale)) * working.RowBytes + Math.Min(mask.Width - 1, (int)(x / scale))] / 255f;
            var guided = GuidedFilter.Upsample(coarse, w, h, image, cancellation);
            // Preserve certain interiors and backgrounds. Only refine the uncertain boundary band.
            try
            {
            using var outer = SelectionMask.Expand(working, settings.Radius);
            using var inner = SelectionMask.Contract(working, settings.Radius);
            var data = (byte*)guided.GetPixels(); var outData = (byte*)outer.GetPixels(); var inData = inner == null ? null : (byte*)inner.GetPixels();
            var guidePixels = new PixelBuffer(image);
            var distance = Math.Max(1, settings.Radius / 2);
            var probes = new[] { (-distance, 0), (distance, 0), (0, -distance), (0, distance) };
            var width = mask.Width; var height = mask.Height; var stride = guided.RowBytes; var sourceStride = working.RowBytes; var outStride = outer.RowBytes; var inStride = inner?.RowBytes ?? 0;
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
              for (var x = 0; x < width; x++)
              {
                var i = (long)y * stride + x;
                if (outData[(long)y * outStride + x] == 0 || inData != null && inData[(long)y * inStride + x] == 255)
                { data[i] = input[(long)y * sourceStride + x]; continue; }
                var edge = false;
                for (var channel = 0; channel < 3 && !edge; channel++)
                {
                    var lo = 1f; var hi = 0f;
                    foreach (var (dx, dy) in probes)
                    {
                        var value = guidePixels.Read(Math.Clamp(x + dx, 0, width - 1), Math.Clamp(y + dy, 0, height - 1))[channel];
                        lo = Math.Min(lo, value); hi = Math.Max(hi, value);
                    }
                    edge = hi - lo > 12 / 255f;
                }
                if (!edge) data[i] = input[(long)y * sourceStride + x];
              }
            });
            }
            catch { guided.Dispose(); throw; }
            working.Dispose(); working = guided;
        }
        cancellation.ThrowIfCancellationRequested();
        if (settings.Shift != 0)
        {
            var shifted = settings.Shift > 0 ? SelectionMask.Expand(working, Math.Min(100, settings.Shift)) : SelectionMask.Contract(working, Math.Min(100, -settings.Shift));
            working.Dispose(); working = shifted ?? Pixels.NewMask(mask.Width, mask.Height);
        }
        if (settings.Feather > 0) { var softened = SelectionMask.Feather(working, (float)Math.Clamp(settings.Feather, 0, 100)); working.Dispose(); working = softened; }
        var contrast = Math.Clamp(settings.Contrast, 0, 100) / 100;
        var pixels = (byte*)working.GetPixels();
        var finalWidth = working.Width; var finalHeight = working.Height; var finalStride = working.RowBytes;
        for (var y = 0; y < finalHeight; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < finalWidth; x++)
            {
                var i = (long)y * finalStride + x;
                pixels[i] = (byte)Math.Clamp((pixels[i] - 127.5) / Math.Max(.01, 1 - contrast) + 127.5, 0, 255);
            }
        }
        Pixels.Invalidate(working); return working;
        }
        catch { working.Dispose(); throw; }
    }

    public static SKBitmap Cutout(SKBitmap image, SKBitmap mask, bool decontaminate) => Cutout(image, mask, decontaminate ? 1 : 0);

    public static SKBitmap Cutout(SKBitmap image, SKBitmap mask, double decontamination, CancellationToken cancellation = default)
    {
        ValidateGrids(image, mask);
        var result = DecontaminateColors(image, mask, decontamination, cancellation);
        var output = new PixelBuffer(result); var alpha = (byte*)mask.GetPixels(); var width = image.Width; var height = image.Height; var stride = mask.RowBytes;
        try
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
            {
                for (var x = 0; x < width; x++) output.Write(x, y, output.Read(x, y) * (alpha[(long)y * stride + x] / 255f));
            });
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>An owned, unmasked RGB copy. Alpha and certain interior pixels are unchanged.
    /// Reverses background mixing when a nearby background sample exists; low-coverage colors use an interior prior.
    /// Already transparent source pixels have unmixed RGB and are deliberately left alone.</summary>
    public static SKBitmap DecontaminateColors(SKBitmap image, SKBitmap mask, double amount, CancellationToken cancellation = default)
    {
        ValidateGrids(image, mask); if (!double.IsFinite(amount)) throw new ArgumentException("Give a finite decontamination amount.");
        amount = Math.Clamp(amount, 0, 1); cancellation.ThrowIfCancellationRequested();
        return MattingColors.Decontaminate(image, mask, amount, cancellation);
    }

    private static void ValidateGrids(SKBitmap image, SKBitmap mask)
    { if (mask.ColorType != SKColorType.Alpha8 || image.Width != mask.Width || image.Height != mask.Height) throw new ArgumentException("Refinement requires matching image and Alpha8 grids."); }
}

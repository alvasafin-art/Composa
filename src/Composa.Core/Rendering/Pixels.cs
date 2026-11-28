using System.Runtime.CompilerServices;
using Composa.Model;
using SkiaSharp;
using Composa.ColorManagement;

namespace Composa.Rendering;

/// <summary>Pixel formats and helpers shared by everything that touches bitmaps.</summary>
public static partial class Pixels
{
    private sealed record ContentExtent(SKRectI Bounds);
    private static readonly ConditionalWeakTable<SKBitmap, ContentExtent> extents = new();

    /// <summary>The non-transparent source rectangle. Committed pixels are immutable, so scan only once per bitmap.</summary>
    public static SKRectI ContentBounds(SKBitmap bitmap) => extents.GetValue(bitmap, static image => new(ScanContent(image))).Bounds;

    private sealed record CoverageFlag(bool HasPixels);
    private static readonly ConditionalWeakTable<SKBitmap, CoverageFlag> coverageFlags = new();

    // The operation picker only needs the first nontransparent pixel, not the full bounds.
    public static bool HasVisiblePixel(SKBitmap bitmap) => extents.TryGetValue(bitmap, out var extent)
        ? !extent.Bounds.IsEmpty : coverageFlags.GetValue(bitmap, static image => new(FindVisiblePixel(image))).HasPixels;

    private static bool FindVisiblePixel(SKBitmap image)
    {
        if (image.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) return !ContentBounds(image).IsEmpty;
        var bytes = image.GetPixelSpan();
        int width = image.Width, height = image.Height, stride = image.RowBytes, bpp = image.BytesPerPixel;
        for (int y = 0; y < height; y++)
        {
            var row = bytes.Slice(y * stride, width * bpp);
            int first = row.IndexOfAnyExcept((byte)0);
            if (first < 0) continue;
            if (bpp == 4)
            {
                for (int a = first / 4 * 4 + 3; a < row.Length; a += 4) if (row[a] != 0) return true;
            }
            else
            {
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(row);
                for (int a = first / 16 * 4 + 3; a < samples.Length; a += 4) if (samples[a] > 0) return true;
            }
        }
        return false;
    }

    private static unsafe SKRectI ScanContent(SKBitmap image)
    {
        if (image.ColorType == SKColorType.RgbaF32) return ScanHighPrecisionContent(image);
        if (image.ColorType != SKColorType.Rgba8888) return new(0, 0, image.Width, image.Height);
        int width = image.Width, height = image.Height, stride = image.RowBytes, left = width, top = height, right = 0, bottom = 0;
        var pixels = (byte*)image.GetPixels();
        for (var y = 0; y < height; y++)
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                if (row[x * 4 + 3] == 0) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x + 1); bottom = y + 1;
            }
        }
        return right <= left ? SKRectI.Empty : new(left, top, right, bottom);
    }
    // An explicit destination space is essential: a null space makes SKCodec retain
    // Adobe RGB / Display P3 channel values without converting their appearance.
    private static readonly SKColorSpace workingSpace = SKColorSpace.CreateSrgb();
    public static SKImageInfo ColorInfo(int width, int height, ColorDepth depth = ColorDepth.Eight, SKColorSpace? space = null) =>
        new(width, height, depth == ColorDepth.Sixteen ? SKColorType.RgbaF32 : SKColorType.Rgba8888, SKAlphaType.Premul, space ?? workingSpace);
    public static SKImageInfo MaskInfo(int width, int height) => new(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

    public static SKBitmap NewColor(int width, int height, ColorDepth depth = ColorDepth.Eight, SKColorSpace? space = null)
    {
        if (depth == ColorDepth.Sixteen && !Model.DocumentLimits.FitsSurface(Math.Max(1, width), Math.Max(1, height), 16))
            throw new InvalidOperationException("The high precision surface exceeds the supported memory or dimensions.");
        var bitmap = new SKBitmap(ColorInfo(Math.Max(1, width), Math.Max(1, height), depth, space));
        TagProfile(bitmap, space == null ? RgbProfile.Srgb : RgbProfile.ForSpace(space));
        Fill(bitmap, 0);
        return bitmap;
    }

    public static SKBitmap NewMask(int width, int height, byte fill = 0)
    {
        var bitmap = new SKBitmap(MaskInfo(Math.Max(1, width), Math.Max(1, height)));
        Fill(bitmap, fill);
        return bitmap;
    }

    internal static SKBitmap NewLayerMask(int width, int height, byte fill = 0)
    {
        var bitmap = LayerPixelStorage.Allocate(MaskInfo(width, height), clear: fill == 0);
        if (fill != 0) bitmap.Erase(new SKColor(0, 0, 0, fill));
        return bitmap;
    }

    /// <summary>
    /// What a mask is past its pixels, and so what new mask area starts as when it grows: black (0) when its border
    /// mostly hides, white (255) otherwise. A reveal-all mask with strokes inside stays white; a hide-all mask stays black.
    /// </summary>
    public static unsafe byte MaskBackground(SKBitmap mask)
    {
        var pixels = (byte*)mask.GetPixels();
        int width = mask.Width, height = mask.Height;
        long total = 0, count = 0;
        for (var y = 0; y < height; y++)
        {
            var row = pixels + (long)y * mask.RowBytes;
            if (y == 0 || y == height - 1)
            {
                for (var x = 0; x < width; x++) total += row[x];
                count += width;
            }
            else
            {
                total += row[0] + row[width - 1];
                count += 2;
            }
        }
        return count == 0 || total * 2 >= count * 255 ? (byte)255 : (byte)0;
    }

    private const int ChunkRows = 256;

    internal static unsafe void Fill(SKBitmap bitmap, byte value)
    {
        var pixels = (byte*)bitmap.GetPixels();
        long stride = bitmap.RowBytes, height = bitmap.Height;
        if (stride * height < 4_000_000) { new Span<byte>(pixels, (int)(stride * height)).Fill(value); return; }
        Parallel.For(0, (int)((height + ChunkRows - 1) / ChunkRows), chunk =>
        {
            var rows = Math.Min(ChunkRows, height - (long)chunk * ChunkRows);
            new Span<byte>(pixels + chunk * ChunkRows * stride, (int)(rows * stride)).Fill(value);
        });
    }

    /// <summary>A view of the requested pixel region retaining the working colour profile.</summary>
    internal static SKBitmap Subset(SKBitmap source, SKRectI area)
    {
        var view = new SKBitmap();
        if (source.ExtractSubset(view, area))
        {
            RetainProfile(source, view);
            return view;
        }
        view.Dispose();
        throw new InvalidOperationException("Cannot read this bitmap region.");
    }

    /// <summary>An independent copy of a bitmap. Far faster than <c>SKBitmap.Copy</c>, which converts pixel by pixel.</summary>
    public static SKBitmap Clone(SKBitmap source) => CloneInto(source, new SKBitmap(source.Info));

    internal static unsafe SKBitmap CloneInto(SKBitmap source, SKBitmap copy)
    {
        RetainProfile(source, copy);
        byte* from = (byte*)source.GetPixels(), to = (byte*)copy.GetPixels();
        long height = source.Height;
        if (source.RowBytes != copy.RowBytes)
        {
            for (var y = 0; y < height; y++) Buffer.MemoryCopy(from + y * (long)source.RowBytes, to + y * (long)copy.RowBytes, copy.RowBytes, Math.Min(source.RowBytes, copy.RowBytes));
            return copy;
        }
        long stride = source.RowBytes;
        if (stride * height < 4_000_000) { Buffer.MemoryCopy(from, to, stride * height, stride * height); return copy; }
        Parallel.For(0, (int)((height + ChunkRows - 1) / ChunkRows), chunk =>
        {
            var offset = chunk * ChunkRows * stride;
            var bytes = Math.Min(ChunkRows, height - (long)chunk * ChunkRows) * stride;
            Buffer.MemoryCopy(from + offset, to + offset, bytes, bytes);
        });
        return copy;
    }

    /// <summary>Copies one bitmap's pixels over another's of the same size and format, then drops what was cached for it.</summary>
    public static unsafe void CopyPixels(SKBitmap from, SKBitmap to)
    {
        if (from.Width != to.Width || from.Height != to.Height || from.ColorType != to.ColorType) throw new ArgumentException("The bitmaps differ in size or format.");
        byte* source = (byte*)from.GetPixels(), destination = (byte*)to.GetPixels();
        long bytes = (long)from.Width * from.BytesPerPixel;
        for (var y = 0; y < from.Height; y++) Buffer.MemoryCopy(source + y * (long)from.RowBytes, destination + y * (long)to.RowBytes, bytes, bytes);
        Invalidate(to);
    }

    // ---- Cached images and reduction pyramids -------------------------------------------------------------------

    /// <summary>Level 0 wraps the bitmap itself without copying; each further level is half the size of the one before.</summary>
    private sealed class Pyramid : IDisposable
    {
        public readonly List<SKBitmap> Bitmaps = [];
        public readonly List<SKImage> Images = [];

        public void Dispose()
        {
            foreach (var image in Images) image.Dispose();
            foreach (var bitmap in Bitmaps.Skip(1)) bitmap.Dispose();
        }
    }

    private static readonly ConditionalWeakTable<SKBitmap, Pyramid> Pyramids = new();
    private static readonly HashSet<SKBitmap> Live = new(ReferenceEqualityComparer.Instance);

    internal static void ForgetDisplayLevels(SKBitmap bitmap)
    {
        // An image already borrowed by another renderer remains valid for that reader.
        lock (bitmap) Pyramids.Remove(bitmap);
    }

    /// <summary>A no-copy image over the bitmap's pixels. Call <see cref="Invalidate"/> after changing them.</summary>
    public static SKImage ImageOf(SKBitmap bitmap)
    {
        var level = 0;
        return Level(bitmap, ref level);
    }

    /// <summary>The image for a reduction level (0 is full size), clamped to the smallest level worth having.</summary>
    internal static SKImage Level(SKBitmap bitmap, ref int level)
    {
        lock (bitmap)
        {
            if (!Pyramids.TryGetValue(bitmap, out var pyramid))
            {
                pyramid = new Pyramid();
                pyramid.Bitmaps.Add(bitmap);
                pyramid.Images.Add(SKImage.FromPixels(bitmap.PeekPixels()));
                Pyramids.Add(bitmap, pyramid);
            }
            level = Math.Max(0, level);
            while (pyramid.Bitmaps.Count <= level)
            {
                var previous = pyramid.Bitmaps[^1];
                if (previous.Width <= 64 || previous.Height <= 64) break;
#if CANVAS_PERFORMANCE
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                var half = Halve(previous);
                pyramid.Bitmaps.Add(half);
                pyramid.Images.Add(SKImage.FromPixels(half.PeekPixels()));
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.StageCompleted("raster pyramid level", started);
#endif
            }
            level = Math.Min(level, pyramid.Images.Count - 1);
            return pyramid.Images[level];
        }
    }

    /// <summary>Raised when a bitmap's pixels changed in place, so other caches keyed on it (thumbnails) can drop it too.</summary>
    public static event Action<SKBitmap>? Invalidated;

    /// <summary>Drops everything cached for a bitmap whose pixels changed.</summary>
    public static void Invalidate(SKBitmap bitmap) => Invalidate(bitmap, null);

    internal static void Invalidate(SKBitmap bitmap, SKRectI? changed)
    {
        LayerPixelStorage.Changed(bitmap, changed);
        InvalidateCaches(bitmap);
    }

    internal static void InvalidateCaches(SKBitmap bitmap)
    {
        extents.Remove(bitmap);
        coverageFlags.Remove(bitmap);
        componentBounds.Remove(bitmap);
        Invalidated?.Invoke(bitmap);
        lock (bitmap)
        {
            if (!Pyramids.TryGetValue(bitmap, out var pyramid)) return;
            Pyramids.Remove(bitmap);
            pyramid.Dispose();
        }
    }

    /// <summary>Marks a bitmap as changing continuously (a stroke in progress): it is drawn without a pyramid until released.</summary>
    public static void SetLive(SKBitmap bitmap, bool live)
    {
        lock (Live) { if (live) Live.Add(bitmap); else Live.Remove(bitmap); }
    }

    internal static bool IsLive(SKBitmap bitmap)
    {
        lock (Live) return Live.Count > 0 && Live.Contains(bitmap);
    }

    /// <summary>Builds the pyramids a reduced render will read, once, before the render fans out over threads.</summary>
    internal static void PrepareLevels(IReadOnlyList<Layer> layers, float scale)
    {
        if (scale >= 0.5f) return;
        Visit(layers);

        void Visit(IReadOnlyList<Layer> layers)
        {
            foreach (var layer in layers)
            {
                if (!layer.Visible) continue;
                Visit(layer.Children);
                // Warp builds its own reduced material.
                if (layer.HasWarp || layer.Pixels == null && layer.Mask == null) continue;
                var layerScale = (double)scale;
                if (layer.Pixels is { } pixels)
                {
                    var info = pixels.Info;
                    layerScale *= Math.Sqrt(Math.Abs(layer.Transform.Width * layer.Transform.Height) /
                        Math.Max(1d, (double)info.Width * info.Height));
                }
                if (layerScale >= .5) continue;
                var level = (int)Math.Floor(Math.Log2(1 / layerScale));
                Prepare(layer.Pixels, level);
                Prepare(layer.Mask, level);
            }
        }

        static void Prepare(SKBitmap? bitmap, int level)
        {
            if (level > 0 && bitmap != null && !IsLive(bitmap)) Level(bitmap, ref level);
        }
    }

    /// <summary>A half-size copy averaging each 2×2 block. Premultiplied pixels average correctly as they are.</summary>
    internal static unsafe SKBitmap Halve(SKBitmap source)
    {
        var info = source.Info;
        int width = Math.Max(1, info.Width / 2), height = Math.Max(1, info.Height / 2), bpp = info.BytesPerPixel;
        var half = new SKBitmap(info.WithSize(width, height));
        if (info.ColorType == SKColorType.RgbaF32)
        {
            var input = new PixelBuffer(source); var output = new PixelBuffer(half);
            int highLastX = info.Width - 1, highLastY = info.Height - 1;
            Parallel.For(0, height, y =>
            {
                int y0 = Math.Min(y * 2, highLastY), y1 = Math.Min(y * 2 + 1, highLastY);
                for (var x = 0; x < width; x++)
                {
                    int x0 = Math.Min(x * 2, highLastX), x1 = Math.Min(x * 2 + 1, highLastX);
                    output.Write(x, y, (input.Read(x0, y0) + input.Read(x1, y0) + input.Read(x0, y1) + input.Read(x1, y1)) * .25f);
                }
            });
            return half;
        }
        byte* from = (byte*)source.GetPixels(), to = (byte*)half.GetPixels();
        int fromStride = source.RowBytes, toStride = half.RowBytes;
        int lastX = info.Width - 1, lastY = info.Height - 1;
        Parallel.For(0, height, y =>
        {
            byte* top = from + (long)Math.Min(y * 2, lastY) * fromStride, bottom = from + (long)Math.Min(y * 2 + 1, lastY) * fromStride;
            var row = to + (long)y * toStride;
            if (bpp == 4 && lastX > 0)
            {
                for (var x = 0; x < width; x++, top += 8, bottom += 8, row += 4)
                {
                    var a = Unsafe.ReadUnaligned<uint>(top);
                    var b = Unsafe.ReadUnaligned<uint>(top + 4);
                    var c = Unsafe.ReadUnaligned<uint>(bottom);
                    var d = Unsafe.ReadUnaligned<uint>(bottom + 4);
                    Unsafe.WriteUnaligned(row, AverageFourBytes(a, b, c, d));
                }
                return;
            }
            for (var x = 0; x < width; x++)
            {
                int a = Math.Min(x * 2, lastX) * bpp, b = Math.Min(x * 2 + 1, lastX) * bpp;
                for (var c = 0; c < bpp; c++) row[x * bpp + c] = (byte)((top[a + c] + top[b + c] + bottom[a + c] + bottom[b + c] + 2) >> 2);
            }
        });
        return half;
    }

    private static uint AverageFourBytes(uint a, uint b, uint c, uint d)
    {
        // Sum alternating channels in 16-bit lanes. Add two for the same rounding as the byte loop.
        const uint lanes = 0x00FF00FF;
        const uint round = 0x00020002;
        var even = ((a & lanes) + (b & lanes) + (c & lanes) + (d & lanes) + round) >> 2;
        var odd = (((a >> 8) & lanes) + ((b >> 8) & lanes) + ((c >> 8) & lanes) + ((d >> 8) & lanes) + round) >> 2;
        return (even & lanes) | ((odd & lanes) << 8);
    }
}

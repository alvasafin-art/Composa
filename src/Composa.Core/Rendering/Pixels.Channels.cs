using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class Pixels
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, Dictionary<RgbChannels, SKRectI>> componentBounds = new();

    public static unsafe SKRectI ComponentBounds(SKBitmap bitmap, RgbChannels channels)
    {
        var cache = componentBounds.GetValue(bitmap, _ => []);
        lock (cache)
        {
            if (cache.TryGetValue(channels, out var found)) return found;
            var mask = bitmap.ColorType == SKColorType.Alpha8; var input = mask ? default : new PixelBuffer(bitmap); var data = (byte*)bitmap.GetPixels();
            var width = bitmap.Width; var height = bitmap.Height; var stride = bitmap.RowBytes;
            int left = width, top = height, right = 0, bottom = 0;
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var p = mask ? default : input.Read(x, y);
                var present = mask ? data[y * stride + x] > 0 : channels.HasFlag(RgbChannels.Red) && p.R > 0 || channels.HasFlag(RgbChannels.Green) && p.G > 0 || channels.HasFlag(RgbChannels.Blue) && p.B > 0;
                if (!present) continue; left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = y + 1;
            }
            return cache[channels] = right <= left ? SKRectI.Empty : new(left, top, right, bottom);
        }
    }

    public static bool IsSingleChannel(RgbChannels channels) => channels is RgbChannels.Red or RgbChannels.Green or RgbChannels.Blue;

    public static SKBitmap Grayscale(SKBitmap source)
    {
        var result = NewLike(source); var input = new PixelBuffer(source); var output = new PixelBuffer(result);
        var width = input.Width; var height = input.Height;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var p = input.Read(x, y); var gray = (p.R * 54 + p.G * 183 + p.B * 19) / 256;
                output.Write(x, y, new(gray, gray, gray, p.A));
            }
        });
        return result;
    }

    /// <summary>An owned grayscale copy of a component, retaining its source precision and alpha.</summary>
    public static SKBitmap ExtractChannel(SKBitmap source, RgbChannels channel, ColorManagement.RgbProfile? outputProfile = null)
    {
        if (!IsSingleChannel(channel)) throw new ArgumentException("Choose one RGB component.");
        var result = outputProfile == null ? NewLike(source) : NewWorkingColor(source.Width, source.Height, DepthOf(source), outputProfile);
        var input = new PixelBuffer(source); var output = new PixelBuffer(result);
        var width = input.Width; var height = input.Height;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var p = input.Read(x, y); var value = channel == RgbChannels.Red ? p.R : channel == RgbChannels.Green ? p.G : p.B;
                output.Write(x, y, new(value, value, value, p.A));
            }
        });
        return result;
    }

    /// <summary>Restores every unselected component and alpha exactly from the original.
    /// Only the caller's uncommitted modified bitmap may be written.</summary>
    public static unsafe void ConstrainChannels(SKBitmap modified, SKBitmap original, RgbChannels channels, SKRectI? area = null)
    {
        if (channels == RgbChannels.All) return;
        if (modified.Width != original.Width || modified.Height != original.Height || modified.ColorType != original.ColorType
            || modified.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) throw new ArgumentException("Channel edits require matching RGB source grids.");
        var bounds = Geometry.Intersect(area ?? new(0, 0, modified.Width, modified.Height), new(0, 0, modified.Width, modified.Height));
        var floating = modified.ColorType == SKColorType.RgbaF32;
        var selected = new[] { channels.HasFlag(RgbChannels.Red), channels.HasFlag(RgbChannels.Green), channels.HasFlag(RgbChannels.Blue) };
        byte* before = (byte*)original.GetPixels(), after = (byte*)modified.GetPixels();
        var beforeStride = original.RowBytes; var afterStride = modified.RowBytes;
        Parallel.For(bounds.Top, bounds.Bottom, y =>
        {
            var src = before + (long)y * beforeStride; var dst = after + (long)y * afterStride;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (floating)
                {
                    var s = (float*)src + x * 4; var d = (float*)dst + x * 4;
                    for (var c = 0; c < 3; c++)
                        if (!selected[c]) d[c] = s[c];
                        else if (d[3] != s[3]) d[c] = d[3] > 0 ? d[c] / d[3] * s[3] : 0;
                    d[3] = s[3];
                }
                else
                {
                    var s = src + x * 4; var d = dst + x * 4;
                    for (var c = 0; c < 3; c++)
                        if (!selected[c]) d[c] = s[c];
                        else if (d[3] != s[3]) d[c] = d[3] > 0 ? (byte)Math.Clamp((d[c] * s[3] + d[3] / 2) / d[3], 0, 255) : (byte)0;
                    d[3] = s[3];
                }
            }
        });
        Invalidate(modified);
    }
}

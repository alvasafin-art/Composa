using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

/// <summary>What Image &gt; Trim looks for along the edges.</summary>
public enum TrimBasedOn
{
    TransparentPixels,
    TopLeftPixelColor,
    BottomRightPixelColor
}

/// <summary>Which edges Trim takes away, and what counts as empty there.</summary>
public sealed record TrimOptions
{
    public TrimBasedOn BasedOn { get; init; } = TrimBasedOn.TransparentPixels;
    public bool Top { get; init; } = true;
    public bool Bottom { get; init; } = true;
    public bool Left { get; init; } = true;
    public bool Right { get; init; } = true;
    /// <summary>How far a pixel's channels may stray from the sampled corner color and still count as it, 0 to 255.</summary>
    public byte Tolerance { get; init; }

    public bool TrimsAny => Top || Bottom || Left || Right;

    public static string DisplayName(TrimBasedOn basedOn) => basedOn switch
    {
        TrimBasedOn.TopLeftPixelColor => "Top Left Pixel Color",
        TrimBasedOn.BottomRightPixelColor => "Bottom Right Pixel Color",
        _ => "Transparent Pixels"
    };
}

public static class ImageTrim
{
    /// <summary>
    /// The part of a flattened picture worth keeping: everything that is not transparent, or not the color of the
    /// chosen corner, with edges that are not being trimmed left where they are. Null when nothing would remain.
    /// </summary>
    public static unsafe SKRectI? Bounds(SKBitmap image, TrimOptions options)
    {
        if (!options.TrimsAny || image.Width == 0 || image.Height == 0) return null;
        if (image.ColorType == SKColorType.RgbaF32) return HighPrecisionBounds(image, options);
        int width = image.Width, height = image.Height;
        var pixels = (byte*)image.GetPixels();
        var stride = image.RowBytes;
        int left = width, top = height, right = 0, bottom = 0;
        if (options.BasedOn == TrimBasedOn.TransparentPixels)
        {
            var content = SelectionMask.Bounds(AlphaOf(image));
            if (content.IsEmpty) return null;
            (left, top, right, bottom) = (content.Left, content.Top, content.Right, content.Bottom);
        }
        else
        {
            var sample = options.BasedOn == TrimBasedOn.TopLeftPixelColor ? pixels : pixels + (long)(height - 1) * stride + (width - 1) * 4;
            int targetR = sample[0], targetG = sample[1], targetB = sample[2], targetA = sample[3];
            int tolerance = options.Tolerance;
            bool Matches(byte* p) => Math.Abs(p[0] - targetR) <= tolerance && Math.Abs(p[1] - targetG) <= tolerance
                && Math.Abs(p[2] - targetB) <= tolerance && Math.Abs(p[3] - targetA) <= tolerance;
            for (var y = 0; y < height; y++)
            {
                var row = pixels + (long)y * stride;
                var first = 0;
                while (first < width && Matches(row + first * 4)) first++;
                if (first == width) continue;
                var last = width;
                while (last > first && Matches(row + (last - 1) * 4)) last--;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
                top = Math.Min(top, y);
                bottom = y + 1;
            }
            if (right == 0) return null; // Every pixel is the corner's color.
        }
        var rect = new SKRectI(options.Left ? left : 0, options.Top ? top : 0, options.Right ? right : width, options.Bottom ? bottom : height);
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    private static SKBitmap AlphaOf(SKBitmap image)
    {
        var alpha = Rendering.Pixels.NewMask(image.Width, image.Height);
        using var canvas = new SKCanvas(alpha);
        canvas.DrawBitmap(image, 0, 0);
        return alpha;
    }

    private static SKRectI? HighPrecisionBounds(SKBitmap image, TrimOptions options)
    {
        var content = Rendering.Pixels.ContentBounds(image);
        if (options.BasedOn != TrimBasedOn.TransparentPixels)
        {
            var pixels = new Rendering.PixelBuffer(image);
            var target = options.BasedOn == TrimBasedOn.TopLeftPixelColor ? pixels.Read(0, 0) : pixels.Read(image.Width - 1, image.Height - 1);
            var tolerance = options.Tolerance / 255f;
            int left = image.Width, top = image.Height, right = 0, bottom = 0;
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var p = pixels.Read(x, y);
                if (Math.Abs(p.R - target.R) <= tolerance && Math.Abs(p.G - target.G) <= tolerance && Math.Abs(p.B - target.B) <= tolerance && Math.Abs(p.A - target.A) <= tolerance) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = y + 1;
            }
            content = right <= left ? SKRectI.Empty : new(left, top, right, bottom);
        }
        if (content.IsEmpty) return null;
        return new SKRectI(options.Left ? content.Left : 0, options.Top ? content.Top : 0,
            options.Right ? content.Right : image.Width, options.Bottom ? content.Bottom : image.Height);
    }
}

public sealed partial class EditorSession
{
    /// <summary>Image &gt; Trim: shrinks the canvas to what the flattened picture shows. False when nothing would be left, or nothing changes.</summary>
    public bool Trim(TrimOptions? options = null)
    {
        options ??= new TrimOptions();
        using var flat = Flatten();
        if (ImageTrim.Bounds(flat, options) is not { } bounds || bounds == document.Bounds) return false;
        Crop(bounds, "Trim");
        return true;
    }

    /// <summary>Shrinks the canvas to the union of every visible layer's pixels.</summary>
    public void TrimCanvas() => Trim();
}

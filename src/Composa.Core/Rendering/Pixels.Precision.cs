using System.Runtime.CompilerServices;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

public static partial class Pixels
{
    private static SKRectI ScanHighPrecisionContent(SKBitmap image)
    {
        var pixels = new PixelBuffer(image);
        int width = pixels.Width, height = pixels.Height, left = width, top = height, right = 0, bottom = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels.Read(x, y).A <= 0) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = y + 1;
        }
        return right <= left ? SKRectI.Empty : new(left, top, right, bottom);
    }

    public static ColorDepth DepthOf(SKBitmap bitmap) => bitmap.ColorType == SKColorType.RgbaF32 ? ColorDepth.Sixteen : ColorDepth.Eight;
    public static bool IsSrgb8(SKBitmap bitmap) => bitmap.ColorType == SKColorType.Rgba8888 && ProfileOf(bitmap).Equals(ColorManagement.RgbProfile.Srgb);

    public static SKBitmap ConvertDepth(SKBitmap source, ColorDepth depth)
    {
        var result = NewColor(source.Width, source.Height, depth, source.ColorSpace);
        RetainProfile(source, result);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }

    /// <summary>An empty bitmap with the source's precision, profile and alpha representation.</summary>
    public static SKBitmap NewLike(SKBitmap source, int? width = null, int? height = null)
    {
        if (source.ColorType == SKColorType.RgbaF32 && !DocumentLimits.FitsSurface(width ?? source.Width, height ?? source.Height, 16))
            throw new InvalidOperationException("The high precision surface exceeds the supported memory or dimensions.");
        var result = new SKBitmap(source.Info.WithSize(width ?? source.Width, height ?? source.Height));
        RetainProfile(source, result);
        Fill(result, 0);
        return result;
    }

    /// <summary>A separate display/codec copy. Never use this to implement an edit of a high precision source.</summary>
    public static SKBitmap DisplayCopy(SKBitmap source)
    {
        var result = NewColor(source.Width, source.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }
}

/// <summary>Normalized premultiplied channels; float working precision also keeps every unsigned 16-bit source level.</summary>
internal readonly record struct PixelValue(float R, float G, float B, float A)
{
    public float this[int channel] => channel switch { 0 => R, 1 => G, 2 => B, 3 => A, _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
    public PixelValue Straight => A <= 0 ? default : new(R / A, G / A, B / A, A);
    public PixelValue Premultiplied => new(R * A, G * A, B * A, A);
    public static PixelValue operator *(PixelValue value, float scale) => new(value.R * scale, value.G * scale, value.B * scale, value.A * scale);
    public static PixelValue operator +(PixelValue a, PixelValue b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
}

/// <summary>Shared normalized access for high precision operations. Callers own an uncommitted bitmap when writing.</summary>
internal readonly unsafe struct PixelBuffer
{
    private readonly byte* data;
    private readonly int stride;
    private readonly bool floating;
    public int Width { get; }
    public int Height { get; }

    internal PixelBuffer(SKBitmap bitmap)
    {
        if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.RgbaF32)) throw new ArgumentException("Expected a working RGB bitmap.", nameof(bitmap));
        data = (byte*)bitmap.GetPixels(); stride = bitmap.RowBytes; floating = bitmap.ColorType == SKColorType.RgbaF32;
        Width = bitmap.Width; Height = bitmap.Height;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PixelValue Read(int x, int y)
    {
        var row = data + (long)y * stride;
        if (floating)
        {
            var p = (float*)row + x * 4;
            return new(p[0], p[1], p[2], p[3]);
        }
        var b = row + x * 4;
        return new(b[0] / 255f, b[1] / 255f, b[2] / 255f, b[3] / 255f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int x, int y, PixelValue value)
    {
        var row = data + (long)y * stride;
        var a = Math.Clamp(value.A, 0, 1);
        if (floating)
        {
            var p = (float*)row + x * 4;
            p[0] = Math.Clamp(value.R, 0, a); p[1] = Math.Clamp(value.G, 0, a); p[2] = Math.Clamp(value.B, 0, a); p[3] = a;
        }
        else
        {
            var b = row + x * 4;
            b[3] = (byte)MathF.Round(a * 255);
            b[0] = (byte)MathF.Round(Math.Clamp(value.R, 0, a) * 255);
            b[1] = (byte)MathF.Round(Math.Clamp(value.G, 0, a) * 255);
            b[2] = (byte)MathF.Round(Math.Clamp(value.B, 0, a) * 255);
        }
    }
}

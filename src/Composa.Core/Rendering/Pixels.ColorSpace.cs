using Composa.ColorManagement;
using Composa.Model;
using SkiaSharp;
using System.Runtime.CompilerServices;

namespace Composa.Rendering;

public static partial class Pixels
{
    private static readonly ConditionalWeakTable<SKBitmap, RgbProfile> colorProfiles = new();
    public static RgbProfile ProfileOf(SKBitmap bitmap) => colorProfiles.TryGetValue(bitmap, out var profile) ? profile : RgbProfile.ForSpace(bitmap.ColorSpace);
    internal static void TagProfile(SKBitmap bitmap, RgbProfile profile) { colorProfiles.Remove(bitmap); colorProfiles.Add(bitmap, profile); }
    private static void RetainProfile(SKBitmap source, SKBitmap copy)
    {
        if (source.ColorType != SKColorType.Alpha8) TagProfile(copy, ProfileOf(source));
    }

    public static SKImageInfo WorkingInfo(int width, int height, ColorDepth depth, RgbProfile profile) => ColorInfo(width, height, depth, profile.Space);

    public static SKBitmap NewWorkingColor(int width, int height, ColorDepth depth, RgbProfile profile)
    {
        var result = NewColor(width, height, depth, profile.Space);
        TagProfile(result, profile); return result;
    }

    /// <summary>Color controls contain channel numbers in the target's working RGB, not implicit sRGB.</summary>
    internal static void FillWorking(SKBitmap target, SKColor color)
    {
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        paint.SetColor((SKColorF)color, target.ColorSpace);
        canvas.DrawPaint(paint); Invalidate(target);
    }

    internal static SKShader WorkingGradient(SKBitmap target, SKPoint from, SKPoint to, SKColor[] colors, bool radial, float? radius = null)
    {
        var values = colors.Select(c => (SKColorF)c).ToArray();
        return radial
            ? SKShader.CreateRadialGradient(from, radius ?? Math.Max(.5f, SKPoint.Distance(from, to)), values, target.ColorSpace, null, SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(from, to, values, target.ColorSpace, null, SKShaderTileMode.Clamp);
    }
    /// <summary>Reinterprets the same channel numbers in another RGB profile. Source pixels remain immutable.</summary>
    internal static SKBitmap AssignProfile(SKBitmap source, RgbProfile profile)
    {
        var result = new SKBitmap(source.Info.WithColorSpace(profile.Space));
        TagProfile(result, profile);
        CopyPixels(source, result); return result;
    }

    /// <summary>A full precision ICC conversion and optional bit-depth conversion in one pass.</summary>
    public static SKBitmap ConvertWorking(SKBitmap source, ColorDepth depth, RgbProfile profile)
    {
        var result = NewColor(source.Width, source.Height, depth, profile.Space);
        TagProfile(result, profile);
        try
        {
            using var input = new SKPixmap(source.Info.WithColorSpace(source.ColorSpace ?? RgbProfile.Srgb.Space), source.GetPixels(), source.RowBytes);
            if (!input.ReadPixels(result.Info, result.GetPixels(), result.RowBytes)) throw new InvalidOperationException("The RGB color conversion could not be completed.");
            ClampColor(result);
            Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>RGB16 stores bounded SDR channels. Skia's float ICC transform can produce extended,
    /// out-of-gamut values; clip those at the working/output boundary, without an 8-bit intermediate.</summary>
    internal static void ClampColor(SKBitmap bitmap, SKRectI? area = null)
    {
        if (bitmap.ColorType != SKColorType.RgbaF32) return;
        var bounds = SKRectI.Intersect(area ?? bitmap.Info.Rect, bitmap.Info.Rect); var pixels = new PixelBuffer(bitmap);
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            var value = pixels.Read(x, y);
            var alpha = float.IsFinite(value.A) ? Math.Clamp(value.A, 0, 1) : 0;
            float Color(float c) => float.IsNaN(c) ? 0 : Math.Clamp(c, 0, alpha);
            pixels.Write(x, y, new(Color(value.R), Color(value.G), Color(value.B), alpha));
        }
    }
}

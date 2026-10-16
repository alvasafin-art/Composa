using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

public enum ExportFormat { Png, Jpeg, Webp, Tiff }

public static class ImageFiles
{
    /// <summary>What Open and Place accept. Photoshop files take the <c>Psd.PsdImport</c> route and SVG files <see cref="SvgImporter"/> rather than <see cref="Load(string)"/>.</summary>
    public static readonly string[] ImportExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".heic", ".heif", ".avif", ".tif", ".tiff", ".svg", ".psd", ".psb"];

    /// <summary>Decodes an image file to RGBA premultiplied pixels, upright according to its EXIF orientation.</summary>
    public static SKBitmap Load(string path, ColorDepth? depth = ColorDepth.Eight)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Load(stream, Path.GetFileName(path), depth);
        }
        catch (InvalidDataException) when (ImageMagick.IsAvailable)
        {
            // Skia has no HEIC, AVIF or TIFF decoder; ImageMagick fills the gap. When it cannot read
            // the file either, its reason is the one worth reporting, since it was the last to try.
            using var stream = new MemoryStream(ImageMagick.Convert(path, depth == ColorDepth.Eight ? ImageMagickOutput.Png : ImageMagickOutput.Png16)!);
            return Load(stream, Path.GetFileName(path), depth);
        }
    }

    public static SKBitmap Load(Stream stream, string name = "image", ColorDepth? depth = ColorDepth.Eight)
    {
        if (depth != ColorDepth.Eight)
        {
            if (!stream.CanSeek)
            {
                using var seekable = new MemoryStream(); stream.CopyTo(seekable); seekable.Position = 0;
                return Load(seekable, name, depth);
            }
            if (Png16Codec.TryDecode(stream, out var origin, name) is { } high) return Upright(high, origin);
        }
        using var codec = SKCodec.Create(stream, out var status)
            ?? throw new InvalidDataException($"{name} could not be read ({status}). Supported formats are PNG, JPEG, WebP, BMP and GIF; HEIC, AVIF and TIFF open when ImageMagick is installed.");
        var info = codec.Info;
        if (!DocumentLimits.FitsSurface(info.Width, info.Height))
            throw new InvalidDataException($"{name} is larger than the supported {DocumentLimits.MaxSide:N0} pixels a side and {DocumentLimits.MaxSurfaceMegapixels} megapixels.");
        var actualDepth = depth ?? ColorDepth.Eight;
        if ((long)info.Width * info.Height * (actualDepth == ColorDepth.Sixteen ? 16 : 4) > DocumentLimits.MaxSurfaceBytes)
            throw new InvalidDataException($"{name} exceeds the supported surface memory.");
        var bitmap = new SKBitmap(Pixels.ColorInfo(info.Width, info.Height, actualDepth));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException($"{name} could not be decoded ({result}).");
        }
        return Upright(bitmap, codec.EncodedOrigin);
    }

    private static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return bitmap;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? bitmap.Height : bitmap.Width, h = swap ? bitmap.Width : bitmap.Height;
        var result = Pixels.NewLike(bitmap, w, h);
        using (var canvas = new SKCanvas(result))
        {
            var matrix = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.SetMatrix(in matrix);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        bitmap.Dispose();
        return result;
    }

    public static byte[] Encode(SKBitmap bitmap, ExportFormat format, int quality = 90, SKColor? matte = null)
    {
        if (format == ExportFormat.Tiff)
        {
            using var tiff = new MemoryStream(); TiffCodec.Write(bitmap, tiff); return tiff.ToArray();
        }
        var source = bitmap;
        SKBitmap? flattened = null;
        if (format == ExportFormat.Jpeg)
        {
            // JPEG has no transparency: composite over the matte (white by default).
            flattened = new SKBitmap(Pixels.ColorInfo(bitmap.Width, bitmap.Height).WithAlphaType(SKAlphaType.Opaque));
            using var canvas = new SKCanvas(flattened);
            canvas.Clear(matte ?? SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
            source = flattened;
        }
        try
        {
            using var data = source.Encode(format switch
            {
                ExportFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                ExportFormat.Webp => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png
            }, Math.Clamp(quality, 1, 100)) ?? throw new IOException("The image could not be encoded.");
            return ImageProfiles.Embed(data.ToArray(), format, source.Width, source.Height);
        }
        finally { flattened?.Dispose(); }
    }

    public static void Save(SKBitmap bitmap, string path, ExportFormat format, int quality = 90, double resolution = 72)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (format == ExportFormat.Tiff)
            {
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                TiffCodec.Write(bitmap, output, resolution); output.Flush(flushToDisk: true);
            }
            else File.WriteAllBytes(temp, Encode(bitmap, format, quality));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static ExportFormat FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ExportFormat.Jpeg,
        ".webp" => ExportFormat.Webp,
        ".tif" or ".tiff" => ExportFormat.Tiff,
        _ => ExportFormat.Png
    };
}

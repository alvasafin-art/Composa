using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// Unpacks channel planes as the Photoshop file format stores them: raw, PackBits run lengths (the usual), or
/// zlib with and without per-row prediction. Written from Adobe's published Photoshop File Formats Specification.
/// </summary>
internal static class PsdChannels
{
    public const int Raw = 0, Rle = 1, Zip = 2, ZipWithPrediction = 3;

    /// <summary>
    /// One plane of <paramref name="width"/> × <paramref name="height"/> bytes from one channel's data. A Large Document
    /// (<paramref name="largeDocument"/>, PSB) stores each PackBits row's byte count in 4 bytes rather than 2. With a
    /// <paramref name="crop"/> only that part of the plane comes back: raw rows are copied in part and PackBits rows
    /// outside it are skipped by their byte counts, so pixels beyond the canvas never cost memory. A zip plane has to be
    /// inflated whole before it can be cut, so there the saving is the color image, not the plane.
    /// </summary>
    private static byte[] DecodeBytes(int compression, int width, int height, ReadOnlySpan<byte> data, bool largeDocument = false, PsdCrop? crop = null)
    {
        var expected = (long)width * height;
        if (expected == 0) return [];
        if (crop is { } part)
        {
            if (part.X < 0 || part.Y < 0 || part.Width < 0 || part.Height < 0 || part.X + part.Width > width || part.Y + part.Height > height) throw PsdException.Truncated();
            if (part.Width == 0 || part.Height == 0) return [];
            if (compression == Raw)
            {
                if (data.Length < expected) throw PsdException.Truncated();
                return Slice(data, width, part);
            }
            if (compression == Rle)
            {
                var cursor = new PsdCursor(data);
                var counts = new int[height];
                for (var row = 0; row < height; row++) counts[row] = RowCount(ref cursor, largeDocument);
                return UnpackRows(data[cursor.Offset..], counts, width, part);
            }
            return Slice(DecodeBytes(compression, width, height, data, largeDocument), width, part);
        }
        switch (compression)
        {
            case Raw:
                if (data.Length < expected) throw PsdException.Truncated();
                return data[..(int)expected].ToArray();
            case Rle:
            {
                var cursor = new PsdCursor(data);
                var counts = new int[height];
                for (var row = 0; row < height; row++) counts[row] = RowCount(ref cursor, largeDocument);
                var plane = new byte[expected];
                UnpackRows(data[cursor.Offset..], counts, width, height, plane, 0);
                return plane;
            }
            case Zip:
            case ZipWithPrediction:
            {
                var plane = Inflate(data, expected);
                if (compression == ZipWithPrediction)
                    for (var row = 0; row < height; row++)
                    {
                        var start = row * width;
                        for (var x = 1; x < width; x++) plane[start + x] = (byte)(plane[start + x] + plane[start + x - 1]);
                    }
                return plane;
            }
            default:
                throw new PsdException("This Photoshop file uses a layer compression method that isn't supported.");
        }
    }

    /// <summary>Converts an 8- or 16-bit sample plane to the editor's 8-bit channels, retaining crop savings.</summary>
    public static byte[] Decode(int compression, int width, int height, ReadOnlySpan<byte> data, bool largeDocument = false, PsdCrop? crop = null, int depth = 8)
    {
        if (depth == 8) return DecodeBytes(compression, width, height, data, largeDocument, crop);
        if (depth != 16) throw new PsdException("Only 8-bit or 16-bit RGB Photoshop files can be imported.");
        var byteWidth = checked(width * 2);
        var byteCrop = crop is { } part ? new PsdCrop(part.X * 2, part.Y, part.Width * 2, part.Height) : (PsdCrop?)null;
        byte[] samples;
        if (compression == ZipWithPrediction)
        {
            samples = DecodeBytes(Zip, byteWidth, height, data, largeDocument);
            // Prediction in a 16-bit file adds unsigned big-endian samples, not the individual bytes.
            for (var y = 0; y < height; y++)
            {
                var previous = 0;
                for (var x = 0; x < width; x++)
                {
                    var i = y * byteWidth + x * 2;
                    previous = (previous + (samples[i] << 8 | samples[i + 1])) & 65535;
                    samples[i] = (byte)(previous >> 8); samples[i + 1] = (byte)previous;
                }
            }
            if (byteCrop is { } cut) samples = Slice(samples, byteWidth, cut);
        }
        else samples = DecodeBytes(compression, byteWidth, height, data, largeDocument, byteCrop);
        return Reduce16(samples);
    }

    internal static byte[] Reduce16(ReadOnlySpan<byte> samples)
    {
        var result = new byte[samples.Length / 2];
        for (var i = 0; i < result.Length; i++) result[i] = (byte)(((samples[i * 2] << 8 | samples[i * 2 + 1]) + 128) / 257);
        return result;
    }

    /// <summary>A PackBits row's byte count: 2 bytes in a PSD, 4 in a PSB.</summary>
    public static int RowCount(ref PsdCursor cursor, bool largeDocument)
    {
        if (!largeDocument) return cursor.U16();
        var count = cursor.U32();
        if (count > int.MaxValue) throw PsdException.Truncated();
        return (int)count;
    }

    /// <summary>The rows of a plane inside the crop, each cut to the crop's columns.</summary>
    private static byte[] Slice(ReadOnlySpan<byte> plane, int width, PsdCrop crop)
    {
        var result = new byte[crop.Width * crop.Height];
        for (var row = 0; row < crop.Height; row++) plane.Slice((crop.Y + row) * width + crop.X, crop.Width).CopyTo(result.AsSpan(row * crop.Width, crop.Width));
        return result;
    }

    /// <summary>PackBits rows through a crop: rows above and below it are stepped over by their byte counts, the rest unpacked one at a time and cut.</summary>
    private static byte[] UnpackRows(ReadOnlySpan<byte> data, int[] counts, int width, PsdCrop crop)
    {
        var plane = new byte[crop.Width * crop.Height];
        var row = new byte[width];
        var offset = 0;
        for (var y = 0; y < counts.Length; y++)
        {
            if (y >= crop.Y && y < crop.Y + crop.Height)
            {
                UnpackRows(data[offset..], counts.AsSpan(y, 1), width, 1, row, 0);
                row.AsSpan(crop.X, crop.Width).CopyTo(plane.AsSpan((y - crop.Y) * crop.Width, crop.Width));
            }
            offset += counts[y];
            if (offset > data.Length) throw PsdException.Truncated();
        }
        return plane;
    }

    /// <summary>PackBits rows into <paramref name="plane"/> from <paramref name="planeOffset"/>; returns the bytes consumed.</summary>
    public static int UnpackRows(ReadOnlySpan<byte> data, ReadOnlySpan<int> counts, int width, int height, byte[] plane, long planeOffset)
    {
        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            var end = offset + counts[row];
            if (end > data.Length) throw PsdException.Truncated();
            var target = planeOffset + (long)row * width;
            var written = 0;
            while (written < width)
            {
                if (offset >= end) throw PsdException.Truncated();
                var n = (sbyte)data[offset++];
                if (n >= 0)
                {
                    var count = n + 1;
                    if (written + count > width || offset + count > end) throw PsdException.Truncated();
                    data.Slice(offset, count).CopyTo(plane.AsSpan((int)(target + written), count));
                    offset += count;
                    written += count;
                }
                else if (n != -128)
                {
                    var count = 1 - n;
                    if (written + count > width || offset >= end) throw PsdException.Truncated();
                    plane.AsSpan((int)(target + written), count).Fill(data[offset++]);
                    written += count;
                }
            }
            offset = end;
        }
        return offset;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data, long expected)
    {
        var plane = new byte[expected];
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            var read = 0;
            while (read < plane.Length)
            {
                var got = zlib.Read(plane, read, plane.Length - read);
                if (got <= 0) break;
                read += got;
            }
            if (read < plane.Length) throw PsdException.Truncated();
        }
        catch (InvalidDataException) { throw PsdException.Truncated(); }
        return plane;
    }

    /// <summary>Premultiplied RGBA pixels from straight planes; a missing alpha means opaque.</summary>
    public static SKBitmap ColorImage(int width, int height, byte[]? red, byte[]? green, byte[]? blue, byte[]? alpha, bool whiteMatte = false)
    {
        var count = (long)width * height;
        if ((red != null && red.Length < count) || (green != null && green.Length < count) || (blue != null && blue.Length < count) || (alpha != null && alpha.Length < count))
            throw PsdException.Truncated();
        var bitmap = Pixels.NewColor(width, height);
        var pixels = bitmap.GetPixelSpan();
        for (long i = 0, p = 0; i < count; i++, p += 4)
        {
            var a = alpha == null ? (byte)255 : alpha[i];
            pixels[(int)p] = ColorByte(red == null ? (byte)0 : red[i], a, whiteMatte);
            pixels[(int)p + 1] = ColorByte(green == null ? (byte)0 : green[i], a, whiteMatte);
            pixels[(int)p + 2] = ColorByte(blue == null ? (byte)0 : blue[i], a, whiteMatte);
            pixels[(int)p + 3] = a;
        }
        Pixels.Invalidate(bitmap);
        return bitmap;
    }

    private static byte ColorByte(byte value, byte alpha, bool whiteMatte) => whiteMatte ? (byte)Math.Clamp(value - (255 - alpha), 0, alpha) : Premultiply(value, alpha);

    private static byte Premultiply(byte value, byte alpha) => alpha == 255 ? value : (byte)((value * alpha + 127) / 255);

    /// <summary>An Alpha8 mask from one gray plane.</summary>
    public static SKBitmap MaskImage(int width, int height, byte[] gray)
    {
        if (gray.Length < (long)width * height) throw PsdException.Truncated();
        var mask = Pixels.NewMask(width, height);
        var target = mask.GetPixelSpan();
        for (var y = 0; y < height; y++) gray.AsSpan(y * width, width).CopyTo(target.Slice(y * mask.RowBytes, width));
        Pixels.Invalidate(mask);
        return mask;
    }
}

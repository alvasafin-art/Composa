using System.Buffers.Binary;
using System.IO.Compression;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

/// <summary>RGB TIFF interchange: 8/16-bit unsigned samples, unassociated alpha, ZIP and embedded ICC.
/// Original writer from TIFF 6.0 and the ICC profile embedding specification. Rows stream to the output.</summary>
internal static class TiffCodec
{
    public static void Write(SKBitmap bitmap, Stream output, double resolution = 72)
    {
        if (!output.CanWrite || !output.CanSeek) throw new ArgumentException("TIFF needs a writable, seekable stream.", nameof(output));
        if (!DocumentLimits.FitsSurface(bitmap.Width, bitmap.Height) || bitmap.ByteCount > DocumentLimits.MaxSurfaceBytes)
            throw new InvalidDataException("The TIFF exceeds the supported surface size.");
        var depth = (int)Pixels.DepthOf(bitmap);
        const int entries = 15;
        const uint bitsOffset = 8 + 2 + entries * 12 + 4;
        const uint formatOffset = bitsOffset + 8;
        const uint xResolutionOffset = formatOffset + 8;
        const uint yResolutionOffset = xResolutionOffset + 8;
        const uint profileOffset = yResolutionOffset + 8;
        var profile = Pixels.ProfileOf(bitmap).Icc;
        var pixelOffset = profileOffset + (uint)((profile.Length + 1) & ~1);
        var start = output.Position;
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        void Entry(ushort tag, ushort type, uint count, uint value)
        { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write((uint)8);
        writer.Write((ushort)entries);
        Entry(256, 4, 1, (uint)bitmap.Width);
        Entry(257, 4, 1, (uint)bitmap.Height);
        Entry(258, 3, 4, bitsOffset);
        Entry(259, 3, 1, 8); // Adobe Deflate, zlib-wrapped.
        Entry(262, 3, 1, 2); // RGB.
        Entry(273, 4, 1, pixelOffset);
        Entry(277, 3, 1, 4);
        Entry(278, 4, 1, (uint)bitmap.Height);
        var byteCountPosition = output.Position + 8;
        Entry(279, 4, 1, 0);
        Entry(282, 5, 1, xResolutionOffset);
        Entry(283, 5, 1, yResolutionOffset);
        Entry(296, 3, 1, 2); // Pixels per inch.
        Entry(338, 3, 1, 2); // Unassociated alpha: stored RGB is straight.
        Entry(339, 3, 4, formatOffset);
        Entry(34675, 7, (uint)profile.Length, profileOffset);
        writer.Write((uint)0); // No next page.
        for (var c = 0; c < 4; c++) writer.Write((ushort)depth);
        for (var c = 0; c < 4; c++) writer.Write((ushort)1); // Unsigned integer samples.
        var dpi = double.IsFinite(resolution) ? Math.Clamp(resolution, 1, 9600) : 72;
        for (var axis = 0; axis < 2; axis++) { writer.Write((uint)Math.Round(dpi * 1000)); writer.Write((uint)1000); }
        writer.Write(profile);
        if (profile.Length % 2 != 0) writer.Write((byte)0);
        var pixels = new PixelBuffer(bitmap);
        var row = new byte[bitmap.Width * 4 * (depth / 8)];
        using (var compressed = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var p = pixels.Read(x, y).Straight;
                    for (var c = 0; c < 4; c++)
                    {
                        var value = Math.Clamp(p[c], 0, 1);
                        if (depth == 16) BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan((x * 4 + c) * 2), (ushort)MathF.Round(value * 65535));
                        else row[x * 4 + c] = (byte)MathF.Round(value * 255);
                    }
                }
                compressed.Write(row);
            }
        }
        var end = output.Position;
        output.Position = byteCountPosition;
        writer.Write(checked((uint)(end - start - pixelOffset)));
        output.Position = end;
    }
}

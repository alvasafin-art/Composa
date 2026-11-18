using System.Buffers.Binary;

namespace Composa.IO;

/// <summary>Physical pixel density in PNG, JPEG (JFIF or Exif) and TIFF headers.</summary>
public static class ImageResolution
{
    public static double Read(string path)
    {
        using var input = File.OpenRead(path);
        try
        {
            Span<byte> header = stackalloc byte[8];
            input.ReadExactly(header);
            double? value = null;
            if (header.SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            {
                Span<byte> density = stackalloc byte[9];
                while (input.Length - input.Position >= 12)
                {
                    input.ReadExactly(header);
                    var size = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (header[4..].SequenceEqual("IDAT"u8)) break;
                    if (size == 9 && header[4..].SequenceEqual("pHYs"u8))
                    {
                        input.ReadExactly(density);
                        if (density[8] == 1) value = BinaryPrimitives.ReadUInt32BigEndian(density) * .0254;
                        break;
                    }
                    input.Seek((long)size + 4, SeekOrigin.Current);
                }
            }
            else if (header[0] == 255 && header[1] == 216)
            {
                input.Position = 2;
                while (input.ReadByte() == 255)
                {
                    int marker;
                    do { marker = input.ReadByte(); } while (marker == 255);
                    if (marker is -1 or 217 or 218) break;
                    if (marker is 1 or >= 208 and <= 215) continue;
                    input.ReadExactly(header[..2]);
                    int length = BinaryPrimitives.ReadUInt16BigEndian(header) - 2;
                    if (length < 0 || length > input.Length - input.Position) break;
                    if (marker is 224 or 225)
                    {
                        var segment = new byte[length]; input.ReadExactly(segment);
                        if (length >= 12 && segment.AsSpan(0,5).SequenceEqual("JFIF\0"u8))
                            value = BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(8)) * (segment[7] switch { 1 => 1d, 2 => 2.54, _ => 0d });
                        if (length > 14 && segment.AsSpan(0,6).SequenceEqual("Exif\0\0"u8))
                        {
                            using var exif = new MemoryStream(segment[6..]);
                            if (Tiff(exif) is { } dpi) { value = dpi; break; }
                        }
                    }
                    else input.Position += length;
                }
            }
            else if (header[..2].SequenceEqual("II"u8) || header[..2].SequenceEqual("MM"u8))
            { input.Position = 0; value = Tiff(input); }
            return value is >= 1 and <= 9600 ? Math.Round(value.Value, 2) : 72;
        }
        catch (EndOfStreamException) { return 72; }
    }

    private static double? Tiff(Stream input)
    {
        Span<byte> h = stackalloc byte[8]; input.ReadExactly(h);
        bool little = h[..2].SequenceEqual("II"u8);
        if (!little && !h[..2].SequenceEqual("MM"u8)) return null;
        ushort Short(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint Long(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
        if (Short(h[2..]) != 42) return null;
        var directory = Long(h[4..]);
        if (directory > input.Length - 2) return null;
        input.Position = directory; input.ReadExactly(h[..2]);
        int count = Short(h), unit = 2;
        uint offset = 0;
        Span<byte> entry = stackalloc byte[12];
        for (int i = 0; i < count; i++)
        {
            input.ReadExactly(entry);
            if (Long(entry[4..]) != 1) continue;
            if (Short(entry) == 282 && Short(entry[2..]) == 5) offset = Long(entry[8..]);
            if (Short(entry) == 296 && Short(entry[2..]) == 3) unit = Short(entry[8..]);
        }
        if (offset == 0 || offset > input.Length - 8 || unit is not (2 or 3)) return null;
        input.Position = offset; input.ReadExactly(h);
        return Long(h[4..]) == 0 ? null : Long(h) / (double)Long(h[4..]) * (unit == 3 ? 2.54 : 1);
    }
}

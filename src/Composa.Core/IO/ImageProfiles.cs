using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Composa.IO.Psd;

namespace Composa.IO;

/// <summary>Explicit output profiles, including formats whose native encoder omits an sRGB ICC chunk.</summary>
internal static class ImageProfiles
{
    internal static byte[] Embed(byte[] encoded, ExportFormat format, int width, int height)
    {
        var profile = PsdExport.SrgbProfileBytes.ToArray();
        using var output = new MemoryStream();
        switch (format)
        {
            case ExportFormat.Png: WritePng(output, encoded, profile); break;
            case ExportFormat.Jpeg: WriteJpeg(output, encoded, profile); break;
            case ExportFormat.Webp: WriteWebp(output, encoded, profile, width, height); break;
            default: return encoded;
        }
        return output.ToArray();
    }

    private static void WritePng(Stream output, byte[] encoded, byte[] profile)
    {
        output.Write(encoded.AsSpan(0, 8));
        for (var offset = 8; offset + 12 <= encoded.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset, 4)));
            var kind = Encoding.ASCII.GetString(encoded, offset + 4, 4);
            if (kind is not ("iCCP" or "sRGB")) output.Write(encoded.AsSpan(offset, length + 12));
            if (kind == "IHDR")
            {
                using var payload = new MemoryStream();
                payload.Write("sRGB\0\0"u8);
                using (var zipped = new ZLibStream(payload, CompressionLevel.Optimal, leaveOpen: true)) zipped.Write(profile);
                WritePngChunk(output, "iCCP", payload.ToArray());
            }
            offset += length + 12;
        }
    }

    private static void WriteJpeg(Stream output, byte[] encoded, byte[] profile)
    {
        output.Write(encoded.AsSpan(0, 2));
        // A segment includes its two length bytes and a 14-byte ICC marker/sequence header.
        const int maximum = 65519;
        var count = (profile.Length + maximum - 1) / maximum;
        Span<byte> segmentLength = stackalloc byte[2];
        for (var index = 0; index < count; index++)
        {
            var start = index * maximum;
            var size = Math.Min(maximum, profile.Length - start);
            output.WriteByte(255); output.WriteByte(226);
            BinaryPrimitives.WriteUInt16BigEndian(segmentLength, (ushort)(size + 16));
            output.Write(segmentLength);
            output.Write("ICC_PROFILE\0"u8);
            output.WriteByte((byte)(index + 1)); output.WriteByte((byte)count);
            output.Write(profile.AsSpan(start, size));
        }
        // Replace any encoder-supplied profile while keeping EXIF and all other segments.
        var offset = 2;
        while (offset + 4 <= encoded.Length && encoded[offset] == 255)
        {
            var marker = encoded[offset + 1];
            if (marker is 218 or 217) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(offset + 2, 2));
            if (length < 2 || offset + length + 2 > encoded.Length) throw new InvalidDataException("Invalid encoded JPEG segment.");
            var isProfile = marker == 226 && length >= 14 && encoded.AsSpan(offset + 4, 12).SequenceEqual("ICC_PROFILE\0"u8);
            if (!isProfile) output.Write(encoded.AsSpan(offset, length + 2));
            offset += length + 2;
        }
        output.Write(encoded.AsSpan(offset));
    }

    private static void WriteWebp(MemoryStream output, byte[] encoded, byte[] profile, int width, int height)
    {
        output.Write("RIFF"u8); output.Write(new byte[4]); output.Write("WEBP"u8);
        var offset = 12;
        if (encoded.AsSpan(12, 4).SequenceEqual("VP8X"u8))
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(16, 4)));
            var extended = encoded.AsSpan(20, size).ToArray();
            extended[0] |= 32;
            WriteRiffChunk(output, "VP8X", extended);
            offset += 8 + size + (size & 1);
        }
        else
        {
            // VP8L records actual alpha use. Premultiplied input can still encode as opaque VP8.
            var alpha = encoded.AsSpan(12, 4).SequenceEqual("VP8L"u8) && (encoded[24] & 16) != 0;
            var extended = new byte[10];
            extended[0] = (byte)(32 | (alpha ? 16 : 0));
            for (var i = 0; i < 3; i++)
            {
                extended[4 + i] = (byte)((width - 1) >> (i * 8));
                extended[7 + i] = (byte)((height - 1) >> (i * 8));
            }
            WriteRiffChunk(output, "VP8X", extended);
        }
        WriteRiffChunk(output, "ICCP", profile);
        while (offset + 8 <= encoded.Length)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 4, 4)));
            var total = 8 + size + (size & 1);
            if (!encoded.AsSpan(offset, 4).SequenceEqual("ICCP"u8)) output.Write(encoded.AsSpan(offset, total));
            offset += total;
        }
        var buffer = output.GetBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), checked((uint)(output.Length - 8)));
    }

    private static void WriteRiffChunk(Stream stream, string kind, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes(kind));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
        stream.Write(length); stream.Write(data);
        if ((data.Length & 1) != 0) stream.WriteByte(0);
    }

    private static void WritePngChunk(Stream stream, string kind, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var type = Encoding.ASCII.GetBytes(kind);
        stream.Write(type); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
    }
}

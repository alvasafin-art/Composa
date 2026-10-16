using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Composa.IO.Psd;
using Composa.ColorManagement;
using Composa.Model;

namespace Composa.IO;

/// <summary>Explicit output profiles, including formats whose native encoder omits an sRGB ICC chunk.</summary>
internal static class ImageProfiles
{
    internal static byte[] Embed(byte[] encoded, ExportFormat format, int width, int height, RgbProfile colorProfile)
    {
        var profile = colorProfile.IccBytes();
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

    internal static RgbProfile? ReadProfile(Stream input)
    {
        if (!input.CanSeek) throw new ArgumentException("Profile reading needs a seekable stream.");
        var start = input.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            if (input.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12) return null;
            input.Position = start;
            if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                input.Position += 8; Span<byte> chunk = stackalloc byte[8]; Span<byte> checksum = stackalloc byte[4];
                while (input.Length - input.Position >= 12)
                {
                    input.ReadExactly(chunk); var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    if (length > input.Length - input.Position - 4) throw new InvalidDataException("The PNG profile chunk is truncated.");
                    if (chunk[4..].SequenceEqual("IDAT"u8)) return null;
                    if (chunk[4..].SequenceEqual("iCCP"u8))
                    {
                        if (length > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The embedded ICC profile is too large.");
                        var data = new byte[length]; input.ReadExactly(data); input.ReadExactly(checksum);
                        if (Png16Codec.Crc(chunk[4..], data) != BinaryPrimitives.ReadUInt32BigEndian(checksum)) throw new InvalidDataException("The PNG ICC chunk is damaged.");
                        var end = Array.IndexOf(data, (byte)0);
                        if (end < 1 || end > 79 || end + 2 >= data.Length || data[end + 1] != 0) throw new InvalidDataException("The PNG ICC profile is invalid.");
                        using var packed = new MemoryStream(data, end + 2, data.Length - end - 2);
                        using var zipped = new ZLibStream(packed, CompressionMode.Decompress);
                        using var profile = new MemoryStream(); var buffer = new byte[8192]; int read;
                        while ((read = zipped.Read(buffer)) > 0)
                        {
                            if (profile.Length + read > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The embedded ICC profile is too large.");
                            profile.Write(buffer, 0, read);
                        }
                        return RgbProfile.FromIcc(profile.ToArray());
                    }
                    input.Position += (long)length + 4;
                }
            }
            else if (header[..2].SequenceEqual(new byte[] { 255, 216 }))
            {
                input.Position += 2; var parts = new SortedDictionary<int, byte[]>(); var expected = 0; var total = 0;
                while (input.Position < input.Length)
                {
                    if (input.ReadByte() != 255) break; int marker; do { marker = input.ReadByte(); } while (marker == 255);
                    if (marker is < 0 or 0xD9 or 0xDA) break;
                    if (marker is 0x01 or >= 0xD0 and <= 0xD8) continue;
                    var a = input.ReadByte(); var b = input.ReadByte(); if (a < 0 || b < 0) throw new InvalidDataException("Truncated JPEG metadata.");
                    var length = (a << 8 | b) - 2;
                    if (length < 0 || length > input.Length - input.Position) throw new InvalidDataException("Truncated JPEG metadata.");
                    if (marker == 0xE2 && length >= 14)
                    {
                        var data = new byte[length]; input.ReadExactly(data);
                        if (!data.AsSpan(0, 12).SequenceEqual("ICC_PROFILE\0"u8)) continue;
                        if (data[12] == 0 || data[13] == 0 || data[12] > data[13] || parts.ContainsKey(data[12]) || expected != 0 && expected != data[13]) throw new InvalidDataException("Invalid JPEG ICC segments.");
                        expected = data[13]; total += data.Length - 14;
                        if (total > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The embedded ICC profile is too large.");
                        parts[data[12]] = data[14..];
                    }
                    else input.Position += length;
                }
                if (expected > 0)
                {
                    if (parts.Count != expected) throw new InvalidDataException("The JPEG ICC profile is incomplete.");
                    using var joined = new MemoryStream(total); foreach (var part in parts.Values) joined.Write(part);
                    return RgbProfile.FromIcc(joined.ToArray());
                }
            }
            else if (header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8))
            {
                input.Position += 12; Span<byte> chunk = stackalloc byte[8];
                while (input.Length - input.Position >= 8)
                {
                    input.ReadExactly(chunk); var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                    if (length > input.Length - input.Position) throw new InvalidDataException("Truncated WebP metadata.");
                    if (chunk[..4].SequenceEqual("ICCP"u8))
                    {
                        if (length > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The embedded ICC profile is too large.");
                        var bytes = new byte[length]; input.ReadExactly(bytes); return RgbProfile.FromIcc(bytes);
                    }
                    input.Position += (long)length + (length & 1);
                }
            }
            return null;
        }
        finally { input.Position = start; }
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
                payload.Write("Composa RGB\0\0"u8);
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

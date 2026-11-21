using System.Buffers.Binary;
using System.IO.Compression;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

/// <summary>
/// Unsigned 16-bit PNG samples decoded without an 8-bit or half-float intermediate. Original implementation of
/// W3C PNG scanline filters and Adam7. Color profiles are interpreted by Skia's metadata reader, not its pixel decoder.
/// </summary>
internal static class Png16Codec
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    internal static SKBitmap? TryDecode(Stream input, string name = "image", bool preserveProfile = false)
        => TryDecode(input, out _, name, preserveProfile);

    internal static SKBitmap? TryDecode(Stream input, out SKEncodedOrigin origin, string name = "image", bool preserveProfile = false)
    {
        try { return Decode(input, out origin, name, preserveProfile); }
        catch (EndOfStreamException) { throw Bad(name, "has truncated PNG data"); }
        catch (OverflowException) { throw Bad(name, "has invalid PNG dimensions or chunk lengths"); }
    }

    private static SKBitmap? Decode(Stream input, out SKEncodedOrigin origin, string name, bool preserveProfile)
    {
        origin = SKEncodedOrigin.TopLeft;
        if (!input.CanSeek) throw new ArgumentException("PNG precision decoding requires a seekable stream.", nameof(input));
        var start = input.Position;
        Span<byte> header = stackalloc byte[33];
        var read = input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        input.Position = start;
        if (read < 33 || !header[..8].SequenceEqual(Signature) || header[24] != 16) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 || !header[12..16].SequenceEqual("IHDR"u8)) throw Bad(name);
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[16..20]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
        if (!DocumentLimits.FitsSurface(width, height)) throw Bad(name, "exceeds the supported image dimensions");
        var colorType = header[25];
        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw Bad(name, "uses an invalid 16-bit color type") };
        if (header[26] != 0 || header[27] != 0 || header[28] > 1) throw Bad(name, "uses an unsupported PNG method");
        // A float working buffer is 16 bytes/pixel. Apply the same byte ceiling as a normal surface.
        if ((long)width * height * 16 > DocumentLimits.MaxSurfaceBytes) throw Bad(name, "needs more memory than a high precision surface can hold");
        var retainedProfile = preserveProfile ? ImageProfiles.ReadProfile(input) : null;
        using var codec = SKCodec.Create(new SKManagedStream(input, disposeManagedStream: false)) ?? throw Bad(name);
        origin = codec.EncodedOrigin;
        var space = retainedProfile?.Space ?? codec.Info.ColorSpace;
        input.Position = start + 8;
        byte[]? transparency = null;
        long dataStart = -1, dataEnd;
        // Project entries are already buffered. For other streams, keep a single pass over the source.
        using var buffered = input is MemoryStream or UnmanagedMemoryStream ? null : new MemoryStream();
        var scratch = new byte[(int)Math.Min(64 * 1024, input.Length - start)];
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> checksum = stackalloc byte[4];
        while (true)
        {
            var chunkStart = input.Position;
            input.ReadExactly(chunkHeader);
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]));
            if (length < 0 || length > input.Length - input.Position - 4) throw Bad(name);
            var kind = chunkHeader[4..8];
            var isData = kind.SequenceEqual("IDAT"u8);
            var isTransparency = kind.SequenceEqual("tRNS"u8);
            if (isData && dataStart < 0) dataStart = chunkStart;
            var crc = UpdateCrc(0xffffffff, kind);
            if (isTransparency) transparency = length is 2 or 6 ? new byte[length] : null;
            for (var remaining = length; remaining > 0;)
            {
                var part = scratch.AsSpan(0, Math.Min(remaining, scratch.Length));
                input.ReadExactly(part);
                crc = UpdateCrc(crc, part);
                if (isData) buffered?.Write(part);
                if (isTransparency && transparency != null) part.CopyTo(transparency);
                remaining -= part.Length;
            }
            input.ReadExactly(checksum);
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(checksum)) throw Bad(name, "has a damaged PNG chunk");
            if (kind.SequenceEqual("IEND"u8)) { dataEnd = chunkStart; break; }
            if (!isData && !kind.SequenceEqual("IHDR"u8) && !kind.SequenceEqual("PLTE"u8) && (kind[0] & 32) == 0)
                throw Bad(name, "contains an unsupported critical PNG chunk");
        }
        if (buffered != null) buffered.Position = 0;
        using var imageData = buffered == null ? new ImageDataStream(input, dataStart < 0 ? dataEnd : dataStart, dataEnd, input.Position) : null;
        using var zipped = new ZLibStream((Stream?)buffered ?? imageData!, CompressionMode.Decompress, leaveOpen: true);
        var source = LayerPixelStorage.Allocate(Pixels.ColorInfo(width, height, ColorDepth.Sixteen, space));
        if (retainedProfile != null) Pixels.TagProfile(source, retainedProfile);
        try
        {
            var target = new PixelBuffer(source);
            var passes = header[28] == 0 ? new[] { (X: 0, Y: 0, Dx: 1, Dy: 1) }
                : [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var pass in passes)
            {
                var columns = Math.Max(0, (width - pass.X + pass.Dx - 1) / pass.Dx);
                if (columns == 0 || pass.Y >= height) continue;
                var row = new byte[columns * channels * 2];
                var previous = new byte[row.Length];
                for (var y = pass.Y; y < height; y += pass.Dy)
                {
                    var filter = zipped.ReadByte();
                    if (filter < 0) throw Bad(name, "has truncated PNG samples");
                    zipped.ReadExactly(row);
                    Unfilter(row, previous, channels * 2, filter, name);
                    for (var i = 0; i < columns; i++)
                    {
                        var samples = row.AsSpan(i * channels * 2, channels * 2);
                        var r = BinaryPrimitives.ReadUInt16BigEndian(samples);
                        var g = channels < 3 ? r : BinaryPrimitives.ReadUInt16BigEndian(samples[2..]);
                        var b = channels < 3 ? r : BinaryPrimitives.ReadUInt16BigEndian(samples[4..]);
                        var alpha = colorType is 4 or 6 ? BinaryPrimitives.ReadUInt16BigEndian(samples[^2..]) : ushort.MaxValue;
                        if (transparency != null && (colorType == 0 && transparency.Length == 2 && r == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                            || colorType == 2 && transparency.Length == 6 && r == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                                && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)))) alpha = 0;
                        var a = alpha / 65535f;
                        target.Write(pass.X + i * pass.Dx, y, new(r / 65535f * a, g / 65535f * a, b / 65535f * a, a));
                    }
                    (row, previous) = (previous, row);
                }
            }
            if (zipped.ReadByte() != -1) throw Bad(name, "has extra PNG samples");
            Pixels.Invalidate(source);
            if (preserveProfile || space == null || space.IsSrgb) return source;
            // Full precision color conversion; the encoded source values and its profile travel together.
            var converted = Pixels.NewLayerColor(width, height, ColorDepth.Sixteen, Composa.ColorManagement.RgbProfile.Srgb);
            try
            {
                using var canvas = new SKCanvas(converted);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(source, 0, 0, paint);
            }
            catch { converted.Dispose(); throw; }
            Pixels.ClampColor(converted);
            source.Dispose();
            return converted;
        }
        catch { source.Dispose(); throw; }
    }

    /// <summary>Reads the payload of the validated IDAT chunks as one stream without concatenating them.</summary>
    private sealed class ImageDataStream : Stream
    {
        private readonly Stream input;
        private readonly long end;
        private readonly long fullEnd;
        private int remaining;

        internal ImageDataStream(Stream input, long start, long end, long fullEnd)
        {
            this.input = input;
            this.end = end;
            this.fullEnd = fullEnd;
            input.Position = start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing) input.Position = fullEnd;
            base.Dispose(disposing);
        }

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            Span<byte> header = stackalloc byte[8];
            while (total < buffer.Length)
            {
                if (remaining == 0)
                {
                    if (input.Position >= end) break;
                    input.ReadExactly(header);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length > end - input.Position - 4) throw new InvalidDataException("Truncated PNG image data.");
                    if (length == 0 || !header[4..].SequenceEqual("IDAT"u8))
                    {
                        input.Position += length + 4L;
                        continue;
                    }
                    remaining = checked((int)length);
                }
                var count = Math.Min(buffer.Length - total, remaining);
                input.ReadExactly(buffer.Slice(total, count));
                total += count;
                remaining -= count;
                if (remaining == 0) input.Position += 4; // CRC already checked.
            }
            return total;
        }
    }

    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, int filter, string name)
    {
        if (filter is < 0 or > 4) throw Bad(name, "uses an invalid PNG filter");
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, above = previous[i], upperLeft = i >= bpp ? previous[i - bpp] : 0;
            var prediction = filter switch { 0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2, _ => Paeth(left, above, upperLeft) };
            row[i] = unchecked((byte)(row[i] + prediction));
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static uint Crc(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> payload) =>
        ~UpdateCrc(UpdateCrc(0xffffffff, kind), payload);

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> payload)
    {
        foreach (var value in payload) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index =>
    {
        var crc = (uint)index;
        for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }).ToArray();

    private static InvalidDataException Bad(string name, string reason = "could not be read") => new($"{name} {reason}.");
}

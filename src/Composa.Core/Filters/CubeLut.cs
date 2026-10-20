using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Composa.Model;

namespace Composa.Filters;

/// <summary>Owned, immutable Cube RGB samples. RGB is red-fastest; a 1D shaper precedes a 3D table.
/// Out-of-domain inputs clamp to the endpoint. No ICC or camera transfer is inferred from the title.</summary>
[JsonConverter(typeof(CubeLutJsonConverter))]
public sealed class CubeLut
{
    private readonly float[] samples, low1, high1, low3, high3;
    public string Title { get; }
    public string Fingerprint { get; }
    public int Size1D { get; }
    public int Size3D { get; }
    public int SampleBytes => samples.Length * sizeof(float);
    public int AssetBytes => SampleBytes + 68 + Encoding.UTF8.GetByteCount(Title);
    public string Description => Size1D > 0 && Size3D > 0 ? $"{Size1D}-point shaper + {Size3D}³" : Size3D > 0 ? $"{Size3D}³ RGB table" : $"{Size1D}-point RGB curves";

    private CubeLut(string title, int size1, int size3, float[] min1, float[] max1, float[] min3, float[] max3, float[] data)
    {
        Title = title; Size1D = size1; Size3D = size3; low1 = min1; high1 = max1; low3 = min3; high3 = max3; samples = data;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new HashStream(hash);
        WriteBinary(output); output.Flush(); Fingerprint = Convert.ToHexString(hash.GetHashAndReset());
    }

    // Hash canonical little-endian data without retaining another full copy of a large table.
    private sealed class HashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public static CubeLut Load(string path, CancellationToken cancellationToken = default)
    {
        using var input = File.OpenRead(path);
        if (input.Length > DocumentLimits.MaxLutTextBytes) throw new InvalidDataException($"The Cube file exceeds {DocumentLimits.MaxLutTextBytes / 1024 / 1024} MB.");
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Read(reader, Path.GetFileNameWithoutExtension(path), cancellationToken);
    }

    public static CubeLut Read(TextReader reader, string fallbackTitle = "Color lookup", CancellationToken cancellationToken = default)
    {
        var title = fallbackTitle; var seen = new HashSet<string>(); var size1 = 0; var size3 = 0;
        float[] min = [0, 0, 0], max = [1, 1, 1]; float[]? min1 = null, max1 = null, min3 = null, max3 = null;
        float[]? data = null; var at = 0; var lineNumber = 0; long characters = 0;
        InvalidDataException Error(string message) => new($"Cube line {lineNumber}: {message}");
        float Number(string token)
        { if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !float.IsFinite(v) || Math.Abs(v) > 1e37f) throw Error("Expected a finite decimal number within ±1e37."); return v; }
        string? NextLine()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = new StringBuilder(); int ch;
            while ((ch = reader.Read()) >= 0)
            {
                if (++characters > DocumentLimits.MaxLutTextBytes) throw Error("The Cube text is too large.");
                if (ch == '\n') break;
                if (ch == '\r') { if (reader.Peek() == '\n') { reader.Read(); characters++; } break; }
                if (line.Length >= DocumentLimits.MaxLutLineLength) throw Error($"A line exceeds {DocumentLimits.MaxLutLineLength} characters.");
                line.Append((char)ch);
            }
            return ch < 0 && line.Length == 0 ? null : line.ToString();
        }
        while (NextLine() is { } raw)
        {
            lineNumber++; var line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("TITLE", StringComparison.Ordinal) && (line.Length == 5 || char.IsWhiteSpace(line[5])))
            {
                if (data != null || !seen.Add("TITLE")) throw Error("Repeated or misplaced TITLE.");
                var text = line[5..].Trim(); var end = text.Length > 1 ? text.IndexOf('"', 1) : -1;
                if (!text.StartsWith('"') || end < 1 || text[(end + 1)..].Trim() is { Length: > 0 } tail && !tail.StartsWith('#')) throw Error("TITLE needs one quoted string.");
                title = text[1..end]; continue;
            }
            var comment = line.IndexOf('#'); if (comment >= 0) line = line[..comment].TrimEnd();
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0];
            if (char.IsAsciiLetter(keyword[0]))
            {
                if (data != null || !seen.Add(keyword)) throw Error("Repeated or misplaced header.");
                switch (keyword)
                {
                    case "LUT_1D_SIZE": case "LUT_3D_SIZE":
                        var limit = keyword == "LUT_1D_SIZE" ? DocumentLimits.MaxLut1DSize : DocumentLimits.MaxLut3DSize;
                        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 2 || size > limit) throw Error($"{keyword} must be 2..{limit}.");
                        if (keyword == "LUT_1D_SIZE") size1 = size; else size3 = size; break;
                    case "DOMAIN_MIN": case "DOMAIN_MAX":
                        if (parts.Length != 4) throw Error("DOMAIN needs three channel values.");
                        var triple = parts.Skip(1).Select(Number).ToArray(); if (keyword == "DOMAIN_MIN") min = triple; else max = triple; break;
                    case "LUT_1D_INPUT_RANGE": case "LUT_3D_INPUT_RANGE":
                        if (parts.Length != 3) throw Error("INPUT_RANGE needs a minimum and maximum.");
                        var lo = Number(parts[1]); var hi = Number(parts[2]);
                        if (keyword == "LUT_1D_INPUT_RANGE") { min1 = [lo, lo, lo]; max1 = [hi, hi, hi]; } else { min3 = [lo, lo, lo]; max3 = [hi, hi, hi]; } break;
                    default: throw Error($"Unsupported keyword {keyword}.");
                }
                continue;
            }
            if (data == null)
            {
                if (size1 == 0 && size3 == 0) throw Error("Missing LUT size.");
                if (size1 == 0 && min1 != null || size3 == 0 && min3 != null) throw Error("INPUT_RANGE has no corresponding table.");
                var hasDomain = seen.Contains("DOMAIN_MIN") || seen.Contains("DOMAIN_MAX");
                if (hasDomain && (min1 != null || min3 != null || size1 > 0 && size3 > 0)) throw Error("DOMAIN and shaper/input-range extensions cannot be combined. Use explicit INPUT_RANGE for each stage.");
                if (size1 > 0) { min1 ??= min; max1 ??= max; } else { min1 = [0, 0, 0]; max1 = [1, 1, 1]; }
                if (size3 > 0) { min3 ??= size1 > 0 ? [0, 0, 0] : min; max3 ??= size1 > 0 ? [1, 1, 1] : max; } else { min3 = [0, 0, 0]; max3 = [1, 1, 1]; }
                ValidateDomains(min1, max1!, min3, max3!);
                var count = checked((size1 + size3 * size3 * size3) * 3);
                if ((long)count * sizeof(float) > DocumentLimits.MaxLutBytes) throw Error("The LUT exceeds the supported memory budget.");
                data = new float[count];
            }
            if (parts.Length != 3 || at + 3 > data.Length) throw Error("Expected exactly one RGB row per table sample.");
            for (var c = 0; c < 3; c++) data[at++] = Number(parts[c]);
        }
        if (data == null || at != data.Length) throw Error("The LUT data is missing or truncated.");
        if (Encoding.UTF8.GetByteCount(title) > DocumentLimits.MaxLutLineLength) throw Error("The LUT title is too long.");
        return new(title, size1, size3, min1!, max1!, min3!, max3!, data);
    }

    private static void ValidateDomains(float[] min1, float[] max1, float[] min3, float[] max3)
    {
        for (var c = 0; c < 3; c++)
            if (!float.IsFinite(min1[c]) || !float.IsFinite(max1[c]) || !float.IsFinite(min3[c]) || !float.IsFinite(max3[c]) || min1[c] >= max1[c] || min3[c] >= max3[c]) throw new InvalidDataException("LUT domain minimum must be below maximum.");
    }

    public (double R, double G, double B) Map(double r, double g, double b)
    {
        double Position(double value, int c, int size, float[] lo, float[] hi) => Math.Clamp((value - lo[c]) / ((double)hi[c] - lo[c]), 0, 1) * (size - 1);
        if (Size1D > 0)
        {
            double Sample(double value, int c)
            { var t = Position(value, c, Size1D, low1, high1); var i = Math.Min((int)t, Size1D - 2); return samples[i * 3 + c] + ((double)samples[(i + 1) * 3 + c] - samples[i * 3 + c]) * (t - i); }
            r = Sample(r, 0); g = Sample(g, 1); b = Sample(b, 2);
        }
        if (Size3D == 0) return (r, g, b);
        var x = Position(r, 0, Size3D, low3, high3); var y = Position(g, 1, Size3D, low3, high3); var z = Position(b, 2, Size3D, low3, high3);
        int ix = Math.Min((int)x, Size3D - 2), iy = Math.Min((int)y, Size3D - 2), iz = Math.Min((int)z, Size3D - 2);
        x -= ix; y -= iy; z -= iz; var start = (Size1D + ix + Size3D * (iy + Size3D * iz)) * 3;
        int rx = 3, gy = Size3D * 3, bz = Size3D * Size3D * 3;
        // Walk the cube's diagonal through the tetrahedron selected by the sorted fractional coordinates.
        int first, second; double a, d, e;
        if (x >= y) { if (y >= z) { first = rx; second = gy; a = x; d = y; e = z; } else if (x >= z) { first = rx; second = bz; a = x; d = z; e = y; } else { first = bz; second = rx; a = z; d = x; e = y; } }
        else { if (x >= z) { first = gy; second = rx; a = y; d = x; e = z; } else if (y >= z) { first = gy; second = bz; a = y; d = z; e = x; } else { first = bz; second = gy; a = z; d = y; e = x; } }
        double Interpolate(int c) => samples[start + c] * (1 - a) + samples[start + first + c] * (a - d) + samples[start + first + second + c] * (d - e) + samples[start + rx + gy + bz + c] * e;
        return (Interpolate(0), Interpolate(1), Interpolate(2));
    }

    public void WriteBinary(Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("CLUT"u8); writer.Write(1); var title = Encoding.UTF8.GetBytes(Title); writer.Write(title.Length); writer.Write(title);
        writer.Write(Size1D); writer.Write(Size3D);
        foreach (var domain in new[] { low1, high1, low3, high3 }) foreach (var value in domain) writer.Write(value);
        if (BitConverter.IsLittleEndian) writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        else foreach (var value in samples) writer.Write(value);
    }

    public static CubeLut ReadBinary(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("CLUT"u8) || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported LUT asset.");
        var length = reader.ReadInt32(); if (length < 0 || length > DocumentLimits.MaxLutLineLength) throw new InvalidDataException("Invalid LUT title length.");
        var titleBytes = reader.ReadBytes(length); if (titleBytes.Length != length) throw new EndOfStreamException(); var title = Encoding.UTF8.GetString(titleBytes);
        int size1 = reader.ReadInt32(), size3 = reader.ReadInt32();
        if (size1 != 0 && (size1 < 2 || size1 > DocumentLimits.MaxLut1DSize) || size3 != 0 && (size3 < 2 || size3 > DocumentLimits.MaxLut3DSize) || size1 == 0 && size3 == 0) throw new InvalidDataException("Invalid LUT dimensions.");
        var domains = new float[4][]; for (var i = 0; i < 4; i++) { domains[i] = new float[3]; for (var c = 0; c < 3; c++) domains[i][c] = reader.ReadSingle(); }
        ValidateDomains(domains[0], domains[1], domains[2], domains[3]);
        var count = checked((size1 + size3 * size3 * size3) * 3);
        if ((long)count * sizeof(float) > DocumentLimits.MaxLutBytes) throw new InvalidDataException("The LUT exceeds the supported memory budget.");
        var samples = new float[count];
        if (BitConverter.IsLittleEndian) input.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        else for (var i = 0; i < count; i++) samples[i] = reader.ReadSingle();
        foreach (var value in samples) if (!float.IsFinite(value) || Math.Abs(value) > 1e37f) throw new InvalidDataException("Invalid LUT sample.");
        if (input.ReadByte() != -1) throw new InvalidDataException("Unexpected trailing LUT data.");
        return new(title, size1, size3, domains[0], domains[1], domains[2], domains[3], samples);
    }
}

public sealed class CubeLutJsonConverter : JsonConverter<CubeLut>
{
    public override CubeLut Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.HasValueSequence && reader.ValueSequence.Length > (DocumentLimits.MaxLutBytes + 8192L) * 4 / 3 || !reader.HasValueSequence && reader.ValueSpan.Length > (DocumentLimits.MaxLutBytes + 8192L) * 4 / 3) throw new JsonException("Invalid LUT data size.");
        using var input = new MemoryStream(reader.GetBytesFromBase64(), writable: false); return CubeLut.ReadBinary(input);
    }
    public override void Write(Utf8JsonWriter writer, CubeLut value, JsonSerializerOptions options)
    { using var output = new MemoryStream(); value.WriteBinary(output); writer.WriteBase64StringValue(output.GetBuffer().AsSpan(0, (int)output.Length)); }
}

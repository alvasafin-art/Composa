using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Composa.Model;
using SkiaSharp;

namespace Composa.ColorManagement;

/// <summary>An immutable, embedded RGB ICC profile shared by document history states.
/// The native space is borrowed by Core renderers; callers must never dispose it.</summary>
public sealed class RgbProfile : IEquatable<RgbProfile>
{
    private readonly byte[] data;
    private readonly string fingerprint;
    internal SKColorSpace Space { get; }
    public string Name { get; }
    public byte[] IccBytes() => data.ToArray();
    internal ReadOnlySpan<byte> Icc => data;

    private RgbProfile(byte[] bytes, SKColorSpace space, string name)
    { data = bytes; Space = space; Name = name; fingerprint = Convert.ToHexString(SHA256.HashData(bytes)); }

    public static RgbProfile FromIcc(ReadOnlySpan<byte> bytes, string? name = null)
    {
        if (bytes.Length < 132 || bytes.Length > DocumentLimits.MaxIccBytes || !bytes.Slice(36, 4).SequenceEqual("acsp"u8) || !bytes.Slice(16, 4).SequenceEqual("RGB "u8))
            throw new InvalidDataException("Choose a supported RGB ICC profile.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (length < 132 || length > bytes.Length) throw new InvalidDataException("The RGB ICC profile is truncated.");
        var owned = bytes[..(int)length].ToArray();
        var space = SKColorSpace.CreateIcc(owned) ?? throw new InvalidDataException("This RGB ICC profile cannot be interpreted by the color engine.");
        var title = string.IsNullOrWhiteSpace(name) ? Description(owned) ?? "Custom RGB" : name.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "Custom RGB";
        return new(owned, space, title[..Math.Min(200, title.Length)]);
    }

    private static string? Description(ReadOnlySpan<byte> bytes)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes[128..]);
        if (count > (bytes.Length - 132) / 12) return null;
        for (var tag = 0; tag < count; tag++)
        {
            var record = bytes.Slice(132 + tag * 12, 12);
            if (!record[..4].SequenceEqual("desc"u8)) continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record[4..]); var length = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            if (offset > bytes.Length || length < 12 || length > bytes.Length - offset) return null;
            var value = bytes.Slice((int)offset, (int)length);
            if (value[..4].SequenceEqual("desc"u8))
            {
                var chars = BinaryPrimitives.ReadUInt32BigEndian(value[8..]);
                if (chars is > 0 and <= 1000 && chars <= value.Length - 12) return Encoding.Latin1.GetString(value.Slice(12, (int)chars)).TrimEnd('\0');
            }
            else if (value[..4].SequenceEqual("mluc"u8) && value.Length >= 28 && BinaryPrimitives.ReadUInt32BigEndian(value[8..]) > 0)
            {
                var chars = BinaryPrimitives.ReadUInt32BigEndian(value[20..]); var start = BinaryPrimitives.ReadUInt32BigEndian(value[24..]);
                if (chars <= 2000 && start <= value.Length && chars <= value.Length - start) return Encoding.BigEndianUnicode.GetString(value.Slice((int)start, (int)chars)).TrimEnd('\0');
            }
        }
        return null;
    }

    private static readonly Lazy<RgbProfile> srgb = new(() =>
    {
        using var input = typeof(RgbProfile).Assembly.GetManifestResourceStream("Composa.IO.Psd.sRGB2014.icc") ?? throw new IOException("The built-in sRGB profile is missing.");
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return FromIcc(bytes.ToArray(), "sRGB");
    });
    private static readonly Lazy<RgbProfile> displayP3 = new(() => FromIcc(MatrixRgbProfile.Write("Display P3", SKColorSpaceXyz.DisplayP3, SKColorSpaceTransferFn.Srgb), "Display P3"));
    private static readonly Lazy<RgbProfile> rgb1998 = new(() => FromIcc(MatrixRgbProfile.Write("Composa RGB (1998)", SKColorSpaceXyz.AdobeRgb,
        new SKColorSpaceTransferFn(563 / 256f, 1, 0, 0, 0, 0, 0)), "RGB (1998)"));
    public static RgbProfile Srgb => srgb.Value;
    public static RgbProfile DisplayP3 => displayP3.Value;
    public static RgbProfile Rgb1998 => rgb1998.Value;
    public static IReadOnlyList<RgbProfile> BuiltIn => [Srgb, DisplayP3, Rgb1998];
    public bool Equals(RgbProfile? other) => other != null && fingerprint == other.fingerprint;
    public override bool Equals(object? obj) => obj is RgbProfile other && Equals(other);
    public override int GetHashCode() => fingerprint.GetHashCode(StringComparison.Ordinal);
}

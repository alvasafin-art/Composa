namespace Composa.Model;

/// <summary>The last file revision adopted by a linked smart object. The cached document remains usable offline.
/// Reading a project restores metadata only; it never opens arbitrary external paths.</summary>
public sealed record SmartObjectLink(string Path, long ByteLength, DateTime LastWriteUtc, string Sha256)
{
    /// <summary>Cached contents were edited without adopting a new disk revision. Automatic refresh must not discard them.</summary>
    public bool HasLocalEdits { get; init; }
    public bool UseMergedAppearance { get; init; }
    /// <summary>Conversion report accepted when this editable PSD revision was adopted.</summary>
    public string? ConversionSignature { get; init; }
    public SmartObjectLink Validated()
    {
        if (string.IsNullOrWhiteSpace(Path) || Path.Length > ushort.MaxValue || Path.Contains('\0') || ByteLength < 0
            || Sha256 is not { Length: 64 } || !Sha256.All(Uri.IsHexDigit)
            || ConversionSignature != null && (ConversionSignature.Length != 64 || !ConversionSignature.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid linked smart object file metadata.");
        return this with { LastWriteUtc = DateTime.SpecifyKind(LastWriteUtc, DateTimeKind.Utc), Sha256 = Sha256.ToUpperInvariant(), ConversionSignature = ConversionSignature?.ToUpperInvariant() };
    }
}

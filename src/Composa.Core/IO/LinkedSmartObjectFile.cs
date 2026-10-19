using System.Security.Cryptography;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

public enum LinkedSourceState { Current, Modified, Missing, LocalEdits, Unavailable }
public sealed record LinkedSourceStatus(LinkedSourceState State, string? Detail = null, SmartObjectLink? Revision = null);

/// <summary>Decoded external content owned until Adopt is called after a successful document edit.</summary>
public sealed class LinkedSmartObjectFile : IDisposable
{
    public static bool CanSave(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cmps" or ".psd" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".tif" or ".tiff";
    public Document Document { get; }
    public SmartObjectLink Link { get; }
    public IReadOnlyList<PsdConversion> Conversions { get; }
    private bool adopted;

    private LinkedSmartObjectFile(Document document, SmartObjectLink link, IReadOnlyList<PsdConversion> conversions)
    { Document = document; Link = link; Conversions = conversions; }

    public void Adopt() => adopted = true;
    public void Dispose()
    {
        if (adopted) return;
        adopted = true;
        var bitmaps = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance); Document.CollectBitmaps(bitmaps);
        foreach (var bitmap in bitmaps) bitmap.Dispose();
    }

    public static LinkedSourceStatus Probe(SmartObjectLink link, bool verifyHash = false)
    {
        if (link.HasLocalEdits) return new(LinkedSourceState.LocalEdits, "The cached contents have local changes.");
        try
        {
            var file = new FileInfo(link.Path); if (!file.Exists) return new(LinkedSourceState.Missing, "The source file cannot be found at its linked path.");
            var length = file.Length; var modified = file.LastWriteTimeUtc; var hash = link.Sha256;
            if (length > PsdExport.MaxFileBytes) return new(LinkedSourceState.Unavailable, "The linked file exceeds the supported file size.");
            if (verifyHash || length != link.ByteLength || modified != link.LastWriteUtc)
            {
                using var stream = File.OpenRead(link.Path);
                hash = Convert.ToHexString(SHA256.HashData(stream));
                file.Refresh();
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified)
                    return new(LinkedSourceState.Unavailable, "The source is still being saved. Composa will try again after its save finishes.");
            }
            var revision = link with { ByteLength = length, LastWriteUtc = modified, Sha256 = hash };
            return new(hash.Equals(link.Sha256, StringComparison.OrdinalIgnoreCase) ? LinkedSourceState.Current : LinkedSourceState.Modified, Revision: revision);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(LinkedSourceState.Unavailable, error.Message); }
    }

    /// <summary>Reads one stable file revision, retaining its source profile/precision. PSD conversion is reported
    /// to the UI; camera RAW must first be developed into an editable project.</summary>
    public static LinkedSmartObjectFile Load(string path, long? pixelBudget = null, bool mergedAppearance = false)
    {
        path = Path.GetFullPath(path);
        if (RawImporter.IsRaw(path)) throw new InvalidDataException("Develop the camera RAW file and save a .cmps project before linking it.");
        var extension = Path.GetExtension(path);
        if (!extension.Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) && !ImageFiles.ImportExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a Composa project, Photoshop document or supported image file.");
        using var stable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stable.Length > PsdExport.MaxFileBytes) throw new InvalidDataException("The linked file exceeds the supported file size.");
        var length = stable.Length; var modified = File.GetLastWriteTimeUtc(path);
        var hash = Convert.ToHexString(SHA256.HashData(stable)); stable.Position = 0;
        var budget = Math.Min(pixelBudget ?? DocumentLimits.DocumentPixelBudget, DocumentLimits.DocumentPixelBudget);
        Document document; IReadOnlyList<PsdConversion> conversions = [];
        if (extension.Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) document = ProjectFile.Read(stable);
        else if (PsdImport.IsPsd(path))
        {
            var import = mergedAppearance ? PsdImport.LoadAppearance(path, budget, preserveProfile: true) : PsdImport.Load(path, budget, preserveProfile: true);
            document = import.ToDocument(); conversions = import.Conversions;
        }
        else
        {
            var pixels = SvgImporter.IsSvg(path) ? SvgImporter.Render(path) : ImageFiles.Load(path, preserveProfile: true);
            document = new(pixels.Width, pixels.Height) { Depth = Pixels.DepthOf(pixels), Profile = Pixels.ProfileOf(pixels) };
            document.InsertAboveActive(Layer.Raster(Path.GetFileNameWithoutExtension(path), pixels));
        }
        var signature = conversions.Count == 0 ? null : Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(conversions)));
        var loaded = new LinkedSmartObjectFile(document, new SmartObjectLink(path, length, modified, hash)
            { UseMergedAppearance = mergedAppearance && PsdImport.IsPsd(path), ConversionSignature = signature }, conversions);
        try
        {
            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("The source changed while it was being read. Try again after its save finishes.");
            if (document.RasterPixels() > budget) throw new InvalidDataException("The linked source exceeds the available document raster budget.");
            return loaded;
        }
        catch { loaded.Dispose(); throw; }
    }

    public static IReadOnlyList<PsdConversion> SaveConversions(Document document, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ProjectFile.Extension) return [];
        if (extension == PsdExport.Extension) return PsdExport.Conversions(document);
        if (!CanSave(path)) throw new InvalidDataException("Save these contents as a .cmps project, PSD, PNG, JPEG, WebP or TIFF.");
        var notes = new List<PsdConversion>();
        if (document.AllLayers().Count() > 1 || document.AllLayers().Any(l => l.IsLive || l.Mask != null || l.Effects != null))
            notes.Add(new("Document", "This image format stores a merged image. Save a .cmps copy to keep editable layers and settings."));
        if (document.WorkingDepth == ColorDepth.Sixteen && extension is ".jpg" or ".jpeg" or ".webp")
            notes.Add(new("Document", "This image format stores 8-bit RGB. Save PNG, TIFF or .cmps to keep high precision."));
        if (extension is ".jpg" or ".jpeg")
        {
            using var flattened = DocumentRenderer.Flatten(document); using var pixels = flattened.PeekPixels();
            if (!pixels.ComputeIsOpaque()) notes.Add(new("Document", "JPEG replaces transparency with white."));
        }
        return notes;
    }

    /// <summary>Writes a caller's immutable snapshot atomically. A known source is replaced only while its bytes
    /// still match the adopted revision; new destinations require explicit overwrite.</summary>
    public static SmartObjectLink Save(Document snapshot, string path, SmartObjectLink? expected = null, bool overwrite = false, bool allowConversion = false)
    {
        path = Path.GetFullPath(path);
        if (!CanSave(path)) throw new InvalidDataException("This source format cannot be saved. Save an editable .cmps copy and relink it.");
        if (expected != null && !string.Equals(Path.GetFullPath(expected.Path), path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The expected source revision belongs to another path.");
        if (expected?.UseMergedAppearance == true) throw new InvalidOperationException("This PSD source was opened as a merged appearance. Save a separate editable copy to keep its original Photoshop layers intact.");
        if (!allowConversion && SaveConversions(snapshot, path).Count > 0) throw new InvalidOperationException("This source save needs conversion review.");
        if (expected != null) VerifyExpectedFile(expected);
        else if (File.Exists(path) && !overwrite) throw new IOException("The destination already exists. Choose another path or explicitly allow replacement.");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ProjectFile.Extension) ProjectFile.Save(snapshot, temporary);
            else if (extension == PsdExport.Extension) PsdExport.Save(snapshot, temporary);
            else
            {
                using var flattened = DocumentRenderer.Flatten(snapshot);
                ImageFiles.Save(flattened, temporary, ImageFiles.FormatFor(path), quality: 95, resolution: snapshot.Resolution);
            }
            SmartObjectLink written;
            using (var stream = File.OpenRead(temporary))
                written = new(path, stream.Length, File.GetLastWriteTimeUtc(temporary), Convert.ToHexString(SHA256.HashData(stream)));
            if (expected != null) VerifyExpectedFile(expected);
            else if (File.Exists(path) && !overwrite) throw new IOException("The destination appeared while saving. Choose another path.");
            File.Move(temporary, path, overwrite: expected != null || overwrite);
            return written;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void VerifyExpectedFile(SmartObjectLink expected)
    {
        if (!File.Exists(expected.Path)) throw new IOException("The source file is missing. Relink it or save these contents to another file.");
        using var stream = File.OpenRead(expected.Path);
        if (stream.Length != expected.ByteLength || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source changed outside Composa. Its current file and your edits were kept. Relink or save an editable copy before replacing it.");
    }
}

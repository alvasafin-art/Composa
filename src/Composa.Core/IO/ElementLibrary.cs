using System.IO.Compression;
using System.Text.Json;
using Composa.ColorManagement;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

public sealed record LibraryEntry(Guid Id, Guid Revision, string Name, string[] Fonts, bool EmbeddedLinkedObjects);

/// <summary>Each shelf entry is one self-contained archive. Listing reads its catalog record only.</summary>
public sealed class ElementLibrary(string folder)
{
    public const int MaxNameLength = 100;
    public const string Extension = ".cmplib";
    private const string CatalogPath = "library/entry.json";
    private const string ThumbnailPath = "library/preview.png";
    private const string Format = "org.composa.library";
    private const int ThumbnailSize = 256;
    private const int MaxThumbnailBytes = (ThumbnailSize * 4 + 1) * ThumbnailSize + 8192;
    private const int MaxCatalogBytes = 512 * 1024;
    private static readonly object Mutations = new();
    private sealed record Catalog(string Format, int Version, LibraryEntry Entry);

    public string Folder { get; } = Path.GetFullPath(folder);

    public (LibraryEntry[] Entries, int Unreadable) List()
    {
        if (!Directory.Exists(Folder)) return ([], 0);
        var entries = new List<LibraryEntry>();
        int unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(Folder, "*" + Extension))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                    throw new InvalidDataException("The library filename is invalid.");
                using var zip = Open(path);
                entries.Add(ReadCatalog(zip, id));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { unreadable++; }
        }
        return (entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Id).ToArray(), unreadable);
    }

    public Document Load(LibraryEntry entry)
    {
        using var stream = OpenArchiveFile(PathFor(entry.Id));
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        CheckRevision(zip, entry);
        return ProjectFile.Read(zip, stream);
    }

    public byte[] LoadThumbnail(LibraryEntry entry)
    {
        using var zip = Open(entry);
        var image = zip.GetEntry(ThumbnailPath) ?? throw new InvalidDataException("The library preview is missing.");
        if (image.Length is < 1 or > MaxThumbnailBytes) throw new InvalidDataException("The library preview size is invalid.");
        var bytes = new byte[image.Length];
        using (var input = image.Open()) input.ReadExactly(bytes);
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
            codec.Info.Width is < 1 or > ThumbnailSize || codec.Info.Height is < 1 or > ThumbnailSize)
            throw new InvalidDataException("The library preview dimensions are invalid.");
        return bytes;
    }

    public LibraryEntry Save(string name, Document element, byte[] thumbnail, bool embeddedLinkedObjects, LibraryEntry? replace = null)
    {
        var entry = new LibraryEntry(replace?.Id ?? Guid.NewGuid(), Guid.NewGuid(), ValidateName(name), FontDependencies(element), embeddedLinkedObjects);
        Publish(entry, replace, zip =>
        {
            ProjectFile.Write(element, zip);
            using var output = zip.CreateEntry(ThumbnailPath, CompressionLevel.NoCompression).Open();
            output.Write(thumbnail);
        });
        return entry;
    }

    public LibraryEntry Rename(LibraryEntry previous, string name)
    {
        var entry = previous with { Name = ValidateName(name), Revision = Guid.NewGuid() };
        Publish(entry, previous, output =>
        {
            using var input = Open(previous);
            foreach (var asset in input.Entries)
            {
                if (asset.FullName == CatalogPath) continue;
                var compression = asset.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var destination = output.CreateEntry(asset.FullName, compression).Open();
                using var source = asset.Open();
                source.CopyTo(destination);
            }
        });
        return entry;
    }

    public void Delete(LibraryEntry entry)
    {
        lock (Mutations)
        {
            using (Open(entry)) { }
            File.Delete(PathFor(entry.Id));
        }
    }

    private string PathFor(Guid id) => Path.Combine(Folder, id.ToString("N") + Extension);

    private void Publish(LibraryEntry entry, LibraryEntry? previous, Action<ZipArchive> write)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(entry.Id);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    write(zip);
                    var catalog = JsonSerializer.SerializeToUtf8Bytes(new Catalog(Format, 1, entry));
                    if (catalog.Length > MaxCatalogBytes) throw new InvalidDataException("The library entry has too many font dependencies.");
                    using var output = zip.CreateEntry(CatalogPath).Open();
                    output.Write(catalog);
                }
                stream.Flush(flushToDisk: true);
            }
            lock (Mutations)
            {
                if (previous != null) using (Open(previous)) { }
                File.Move(temp, path, overwrite: previous != null);
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static FileStream OpenArchiveFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static ZipArchive Open(string path)
    {
        var stream = OpenArchiveFile(path);
        try { return new ZipArchive(stream, ZipArchiveMode.Read); }
        catch { stream.Dispose(); throw; }
    }

    private ZipArchive Open(LibraryEntry entry)
    {
        var zip = Open(PathFor(entry.Id));
        try
        {
            CheckRevision(zip, entry);
            return zip;
        }
        catch { zip.Dispose(); throw; }
    }

    private static void CheckRevision(ZipArchive zip, LibraryEntry entry)
    {
        if (ReadCatalog(zip, entry.Id).Revision != entry.Revision)
            throw new IOException("This library entry changed. Select it again and retry.");
    }

    private static LibraryEntry ReadCatalog(ZipArchive zip, Guid id)
    {
        var catalog = zip.GetEntry(CatalogPath) ?? throw new InvalidDataException("The library catalog record is missing.");
        if (catalog.Length is < 1 or > MaxCatalogBytes) throw new InvalidDataException("The library catalog record size is invalid.");
        using var input = catalog.Open();
        if (JsonSerializer.Deserialize<Catalog>(input) is not { Format: Format, Version: 1, Entry: { } entry } ||
            id == Guid.Empty || entry.Id != id || entry.Revision == Guid.Empty || entry.Fonts == null ||
            entry.Fonts.Any(font => string.IsNullOrWhiteSpace(font)))
            throw new InvalidDataException("The library catalog record is invalid.");
        ValidateName(entry.Name);
        return entry;
    }

    public static string? NormalizeName(string? value)
    {
        var name = value?.Trim();
        return name is { Length: > 0 and <= MaxNameLength } && !name.Any(char.IsControl) ? name : null;
    }

    private static string ValidateName(string? name) =>
        NormalizeName(name) ?? throw new InvalidDataException("Enter a library name between 1 and 100 characters.");

    public static string[] FontDependencies(Document element)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<SmartObjectSource>(ReferenceEqualityComparer.Instance);
        void Add(string family) { if (!string.IsNullOrWhiteSpace(family)) families.Add(family); }
        void Collect(Document value)
        {
            foreach (var definition in value.Styles.Values)
                if (definition.Text is { } style) Add(style.Face.FontFamily);
            foreach (var layer in value.AllLayers())
            {
                if (layer.Text is { } text)
                {
                    Add(text.FontFamily);
                    if (text.FontRuns != null)
                        foreach (var run in text.FontRuns) Add(run.FontFamily);
                }
                if (layer.SmartObject is { } source && sources.Add(source)) Collect(source.OpenDocument());
            }
        }
        Collect(element);
        return families.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static byte[] RenderThumbnail(Document element)
    {
        var bounds = SKRect.Empty;
        foreach (var layer in element.AllLayers())
        {
            var visible = layer.VisibleBounds;
            if (visible.IsEmpty || !element.IsEffectivelyVisible(layer)) continue;
            if (bounds.IsEmpty) bounds = visible;
            else bounds.Union(visible);
        }
        if (bounds.IsEmpty) bounds = element.Bounds;
        var scale = (ThumbnailSize - 12) / Math.Max(bounds.Width, bounds.Height);
        var center = ThumbnailSize / (2 * scale);
        using var preview = Pixels.NewWorkingColor(ThumbnailSize, ThumbnailSize, ColorDepth.Eight, RgbProfile.Srgb);
        DocumentRenderer.Render(element, preview, new SKRectI(0, 0, ThumbnailSize, ThumbnailSize),
            new RenderView(scale, new SKPoint(bounds.MidX - center, bounds.MidY - center)));
        using var png = preview.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}

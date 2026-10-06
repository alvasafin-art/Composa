using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

/// <summary>
/// The project format: a zip archive holding <c>manifest.json</c> and an <c>images/</c> folder with one PNG per
/// layer (and one grayscale PNG per mask). Source pixels are stored untouched; transforms stay separate.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".cmps";
    public const string Format = "org.composa.project";
    /// <summary>
    /// The format version new saves write, and the highest one <see cref="Read"/> accepts. 1 was the first release,
    /// 2 added guides, 3 added the Gaussian Blur, Motion Blur and Add Noise adjustment layers and the Inner Glow effect,
    /// 4 added letters in their own colors (<see cref="TextStyle.ColorRuns"/>), 5 letters in their own faces
    /// (<see cref="TextStyle.FontRuns"/>), 6 optional layer tags, 7 embedded smart object documents, 8 installed font styles,
    /// 9 character size runs and separate fill opacity for imported Photoshop effects.
    /// </summary>
    public const int Version = 10;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class Manifest
    {
        public string Format { get; set; } = ProjectFile.Format;
        public int Version { get; set; } = ProjectFile.Version;
        public string ColorSpace { get; set; } = "sRGB";
        public int Width { get; set; }
        public int Height { get; set; }
        public double Resolution { get; set; } = 72;
        public Guid? ActiveLayerId { get; set; }
        public List<LayerRecord> Layers { get; set; } = [];
        /// <summary>Alignment guides; absent on version 1 files.</summary>
        public List<Guide>? Guides { get; set; }
        public Dictionary<string, string>? AlphaChannels { get; set; }
        public Dictionary<string, SmartRecord>? SmartObjects { get; set; }
    }

    private sealed class SmartRecord
    {
        public Guid Id { get; set; }
        public Manifest Content { get; set; } = new();
    }

    private sealed class LayerRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<string>? Tags { get; set; }
        public LayerKind Kind { get; set; }
        public bool Visible { get; set; } = true;
        public double Opacity { get; set; } = 1;
        public double FillOpacity { get; set; } = 1;
        public BlendMode Blend { get; set; }
        public LayerTransform? Transform { get; set; }
        public string? ImageFile { get; set; }
        public string? MaskFile { get; set; }
        public bool? MaskEnabled { get; set; }
        public bool? Clipped { get; set; }
        public bool? Collapsed { get; set; }
        public LayerLocks Locks { get; set; }
        public string? FilterSourceFile { get; set; }
        public SmartFilter[]? SmartFilters { get; set; }
        public int FilterPaddingX { get; set; }
        public int FilterPaddingY { get; set; }
        public VectorPath? VectorMask { get; set; }
        public bool? VectorMaskEnabled { get; set; }
        public Adjustment? Adjustment { get; set; }
        public ShapeStyle? Shape { get; set; }
        public TextStyle? Text { get; set; }
        public LayerEffects? Effects { get; set; }
        public List<LayerRecord>? Children { get; set; }
        public string? SmartObject { get; set; }
    }

    public static void Save(Document document, string path)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = File.Create(temp)) Write(document, stream);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Write(Document document, Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var written = new Dictionary<SKBitmap, string>(ReferenceEqualityComparer.Instance);
        var sources = new Dictionary<SmartObjectSource, string>(ReferenceEqualityComparer.Instance);
        var objects = new Dictionary<string, SmartRecord>();

        string StoreSource(SmartObjectSource source)
        {
            if (sources.TryGetValue(source, out var existing)) return existing;
            var key = "object-" + sources.Count;
            sources.Add(source, key);
            var record = new SmartRecord { Id = source.Id }; objects.Add(key, record);
            record.Content = RecordDocument(source.OpenDocument());
            return key;
        }

        string Store(SKBitmap bitmap, string name)
        {
            if (written.TryGetValue(bitmap, out var existing)) return existing;
            var entry = zip.CreateEntry("images/" + name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            var bytes = bitmap.ColorType == SKColorType.Alpha8 ? EncodeMask(bitmap) : ImageFiles.Encode(bitmap, ExportFormat.Png);
            output.Write(bytes);
            return written[bitmap] = name;
        }

        LayerRecord Record(Layer layer) => new()
        {
            Id = layer.Id, Name = layer.Name, Tags = layer.Tags.Count > 0 ? layer.Tags.Order().ToList() : null,
            Kind = layer.Kind, Visible = layer.Visible, Opacity = layer.Opacity, FillOpacity = layer.FillOpacity, Blend = layer.Blend,
            Transform = layer.Pixels != null ? layer.Transform : null,
            ImageFile = layer.Pixels != null && (!layer.IsSmartObject || layer.FilterSource != null) ? Store(layer.Pixels, $"{written.Count}.png") : null,
            FilterSourceFile = layer.FilterSource != null ? Store(layer.FilterSource, $"{written.Count}.filter-source.png") : null,
            SmartFilters = layer.SmartFilters.Length == 0 ? null : layer.SmartFilters,
            FilterPaddingX = layer.FilterPaddingX, FilterPaddingY = layer.FilterPaddingY,
            VectorMask = layer.VectorMask, VectorMaskEnabled = layer.VectorMask == null ? null : layer.VectorMaskEnabled,
            MaskFile = layer.Mask != null ? Store(layer.Mask, $"{written.Count}.mask.png") : null,
            MaskEnabled = layer.Mask != null ? layer.MaskEnabled : null, Locks = layer.Locks,
            Clipped = layer.Clipped ? true : null,
            Collapsed = layer.Collapsed ? true : null,
            Adjustment = layer.Adjustment, Shape = layer.Shape, Text = layer.Text, Effects = layer.Effects,
            Children = layer.IsGroup ? layer.Children.Select(Record).ToList() : null,
            SmartObject = layer.SmartObject is { } source ? StoreSource(source) : null
        };

        Manifest RecordDocument(Document value) => new()
        {
            Width = value.Width, Height = value.Height, Resolution = value.Resolution,
            ActiveLayerId = value.ActiveLayerId, Layers = value.Layers.Select(Record).ToList(),
            Guides = value.Guides.Count > 0 ? value.Guides.ToList() : null,
            AlphaChannels = value.AlphaChannels.Count == 0 ? null : value.AlphaChannels.ToDictionary(p => p.Key, p => Store(p.Value, $"{written.Count}.channel.png"))
        };
        var manifest = RecordDocument(document);
        manifest.SmartObjects = objects.Count > 0 ? objects : null;
        using var manifestStream = zip.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifestStream, manifest, Json);
    }

    public static Document Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static Document Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("This is not a Composa project: it has no manifest.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("The project's manifest is too large.");
        Manifest manifest;
        using (var manifestStream = entry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(manifestStream, Json) ?? throw new InvalidDataException("The project's manifest is empty.");
        if (manifest.Format != Format) throw new InvalidDataException("This is not a Composa project.");
        if (manifest.Version > Version) throw new InvalidDataException($"This project uses format version {manifest.Version}; this app supports up to version {Version}.");
        if (manifest.Width < 1 || manifest.Height < 1 || manifest.Width > Document.MaxSide || manifest.Height > Document.MaxSide)
            throw new InvalidDataException("The project's canvas size is invalid.");

        var cache = new Dictionary<string, SKBitmap>();
        var sources = new Dictionary<string, SmartObjectSource>();
        var visiting = new HashSet<string>();
        long allocatedPixels = 0;
        var count = 0;

        SKBitmap Fetch(string name, bool mask)
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("The project refers to an unsafe path.");
            if (cache.TryGetValue(name, out var cached)) return cached;
            var image = zip.GetEntry("images/" + name) ?? throw new InvalidDataException($"An image inside the project is missing ({name}).");
            using var buffer = new MemoryStream();
            using (var input = image.Open()) input.CopyTo(buffer);
            buffer.Position = 0;
            var bitmap = mask ? DecodeMask(buffer) : ImageFiles.Load(buffer, name);
            allocatedPixels += (long)bitmap.Width * bitmap.Height;
            cache[name] = bitmap;
            if (allocatedPixels > DocumentLimits.DocumentPixelBudget) throw new InvalidDataException("The project exceeds the document raster budget.");
            return bitmap;
        }

        SmartObjectSource Source(string key)
        {
            if (sources.TryGetValue(key, out var cached)) return cached;
            if (!visiting.Add(key) || visiting.Count > SmartObjectSource.MaxDepth)
                throw new InvalidDataException("The project contains cyclic or overly nested smart objects.");
            if (manifest.SmartObjects == null || !manifest.SmartObjects.TryGetValue(key, out var record))
                throw new InvalidDataException("Embedded smart object content is missing.");
            var value = BuildDocument(record.Content);
            var source = SmartObjectSource.Create(value, record.Id == Guid.Empty ? null : record.Id);
            sources.Add(key, source); visiting.Remove(key);
            allocatedPixels += (long)source.Width * source.Height;
            if (allocatedPixels > DocumentLimits.DocumentPixelBudget) throw new InvalidDataException("The project exceeds the document raster budget.");
            return source;
        }

        Layer Build(LayerRecord record, int depth)
        {
            if (++count > 10_000 || depth > 64) throw new InvalidDataException("The project has too many layers.");
            var layer = new Layer
            {
                Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id, Name = record.Name, Kind = record.Kind, Visible = record.Visible,
                Opacity = double.IsFinite(record.Opacity) ? Math.Clamp(record.Opacity, 0, 1) : 1, FillOpacity = double.IsFinite(record.FillOpacity) ? Math.Clamp(record.FillOpacity, 0, 1) : 1, Blend = record.Blend,
                MaskEnabled = record.MaskEnabled ?? true, Clipped = record.Clipped ?? false, Collapsed = record.Collapsed ?? false, Locks = record.Locks & LayerLocks.All,
                Adjustment = record.Adjustment, Shape = record.Shape?.Clamped(), Text = record.Text?.Clamped(),
                VectorMask = record.VectorMask?.Normalized(), VectorMaskEnabled = record.VectorMaskEnabled ?? true
            };
            if (record.Tags != null)
                foreach (var tag in record.Tags.Take(64)) if (LayerTags.Normalize(tag) is { } normalized) layer.Tags.Add(normalized);
            if (record.SmartObject != null)
            {
                if (record.Kind != LayerKind.Raster || record.Text != null || record.Shape != null)
                    throw new InvalidDataException("A smart object cannot also be a group, adjustment, text or shape layer.");
                layer.SmartObject = Source(record.SmartObject); layer.Pixels = layer.SmartObject.Preview;
                layer.Transform = IsUsable(record.Transform) ? record.Transform! : LayerTransform.Identity(layer.Pixels.Width, layer.Pixels.Height);
                if (record.Effects is { } effects && !effects.IsEmpty) layer.Effects = effects.Clamped();
            }
            else if (record.ImageFile != null && record.Kind == LayerKind.Raster)
            {
                layer.Pixels = Fetch(record.ImageFile, mask: false);
                layer.Transform = IsUsable(record.Transform) ? record.Transform! : LayerTransform.Identity(layer.Pixels.Width, layer.Pixels.Height);
                if (record.Effects is { } effects && !effects.IsEmpty) layer.Effects = effects.Clamped();
            }
            else if (record.Kind == LayerKind.Raster) throw new InvalidDataException($"Layer \"{record.Name}\" has no image.");
            if (record.Kind == LayerKind.Adjustment && record.Adjustment == null) throw new InvalidDataException($"Adjustment layer \"{record.Name}\" has no settings.");
            if (record.MaskFile != null) layer.Mask = Fetch(record.MaskFile, mask: true);
            if (record.FilterSourceFile != null)
            {
                layer.FilterSource = Fetch(record.FilterSourceFile, mask: false);
                layer.SmartFilters = record.SmartFilters?.Take(64).Where(f => Enum.IsDefined(f.Settings.Kind)).ToArray() ?? [];
                layer.FilterPaddingX = Math.Clamp(record.FilterPaddingX, 0, DocumentLimits.MaxSide);
                layer.FilterPaddingY = Math.Clamp(record.FilterPaddingY, 0, DocumentLimits.MaxSide);
                if (record.ImageFile != null) layer.Pixels = Fetch(record.ImageFile, mask: false);
            }
            if (record.Kind == LayerKind.Group && record.Children != null)
                foreach (var child in record.Children) layer.Children.Add(Build(child, depth + 1));
            return layer;
        }

        Document BuildDocument(Manifest value)
        {
            if (!DocumentLimits.FitsSurface(value.Width, value.Height) || value.Format != Format || value.Version > Version)
                throw new InvalidDataException("Invalid embedded document size or format.");
            var result = new Document(value.Width, value.Height) { Resolution = double.IsFinite(value.Resolution) ? Math.Clamp(value.Resolution, 1, 9600) : 72 };
            foreach (var record in value.Layers) result.Layers.Add(Build(record, 0));
            if (value.AlphaChannels != null)
                foreach (var pair in value.AlphaChannels.Take(64))
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 200) result.AlphaChannels[pair.Key] = Fetch(pair.Value, mask: true);
            if (value.Guides != null)
                foreach (var guide in value.Guides.Where(g => g.IsValid).Take(1000))
                    result.Guides.Add(guide.Id == Guid.Empty ? guide with { Id = Guid.NewGuid() } : guide);
            var active = value.ActiveLayerId is { } id && result.Find(id) != null ? id : result.Layers.LastOrDefault()?.Id;
            result.SetActive(active); return result;
        }
        try { return BuildDocument(manifest); }
        catch
        {
            var allocated = cache.Values.Concat(sources.Values.Select(source => source.Preview)).Distinct();
            foreach (var bitmap in allocated) bitmap.Dispose();
            throw;
        }
    }

    /// <summary>A damaged file must not feed non-finite or degenerate placements into rendering.</summary>
    private static bool IsUsable(LayerTransform? t) =>
        t != null && new[] { t.X, t.Y, t.Width, t.Height, t.Rotation }.All(double.IsFinite)
        && t.Width >= 1 && t.Height >= 1 && t.Width <= 1_000_000 && t.Height <= 1_000_000 && Math.Abs(t.X) <= 10_000_000 && Math.Abs(t.Y) <= 10_000_000
        && (t.Distort == null || (t.Distort.Length == 8 && t.Distort.All(float.IsFinite)));

    private static byte[] EncodeMask(SKBitmap mask)
    {
        using var gray = new SKBitmap(new SKImageInfo(mask.Width, mask.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        CopyRows(mask, gray);
        using var data = gray.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("A mask could not be encoded.");
        return data.ToArray();
    }

    private static SKBitmap DecodeMask(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("A mask inside the project is damaged.");
        if (!DocumentLimits.FitsSurface(codec.Info.Width, codec.Info.Height)) throw new InvalidDataException("The project mask is too large.");
        using var gray = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        if (codec.GetPixels(gray.Info, gray.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("A mask inside the project is damaged.");
        var mask = Pixels.NewMask(gray.Width, gray.Height);
        CopyRows(gray, mask);
        return mask;
    }

    private static unsafe void CopyRows(SKBitmap from, SKBitmap to)
    {
        for (var y = 0; y < from.Height; y++)
            Buffer.MemoryCopy((byte*)from.GetPixels() + (long)y * from.RowBytes, (byte*)to.GetPixels() + (long)y * to.RowBytes, to.RowBytes, from.Width);
    }
}

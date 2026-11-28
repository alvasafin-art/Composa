using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using Composa.ColorManagement;

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
    /// 9 character size runs and separate fill opacity for imported Photoshop effects, 10 professional editing data,
    /// 11 explicit Normal/Pass Through groups, Blend If ranges and non-destructive raster mask properties,
    /// 12 retained interpolating Warp surfaces; 13 extended color adjustment data, custom hue ranges and embedded LUTs. Version 14 adds typography and justified paragraphs. Version 15 stores formatting ranges within a text layer. Version 16 adds parametric polygons, stars and independent corner radii.
    /// Version 17 retains auto layout groups, item sizing and image frames. Version 18 stores shared layer styles.
    /// </summary>
    public const int Version = 18;

    // CMPS is written during interactive saves and crash recovery. All PNG filters stay available,
    // while level 1 spends less time searching for matches in already filtered lossless raster data.
    private static readonly SKPngEncoderOptions RasterPng = new(SKPngEncoderFilterFlags.AllFilters, 1);

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
        public string? ColorProfileFile { get; set; }
        public ColorDepth BitDepth { get; set; } = ColorDepth.Eight;
        public int Width { get; set; }
        public int Height { get; set; }
        public double Resolution { get; set; } = 72;
        public CanvasColorMode? CanvasColorMode { get; set; }
        public CanvasLayout? CanvasLayout { get; set; }
        public Guid? ActiveLayerId { get; set; }
        public List<LayerRecord> Layers { get; set; } = [];
        public List<LayerStyleDefinition>? Styles { get; set; }
        public List<PromptRegion>? PromptRegions { get; set; }
        /// <summary>Alignment guides; absent on version 1 files.</summary>
        public List<Guide>? Guides { get; set; }
        public Dictionary<string, string>? AlphaChannels { get; set; }
        public Dictionary<string, SmartRecord>? SmartObjects { get; set; }
    }

    private sealed class SmartRecord
    {
        public Guid Id { get; set; }
        public SmartObjectLink? Link { get; set; }
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
        public BlendIfSettings? BlendIf { get; set; }
        public BlendMode Blend { get; set; }
        public LayerTransform? Transform { get; set; }
        public WarpMesh? Warp { get; set; }
        public string? ImageFile { get; set; }
        public string? MaskFile { get; set; }
        public bool MaskLinked { get; set; } = true;
        public bool MaskFollowsWarp { get; set; } = true;
        public LayerTransform? MaskTransform { get; set; }
        public byte MaskDefault { get; set; }
        public bool? MaskEnabled { get; set; }
        public double MaskDensity { get; set; } = 1;
        public double MaskFeather { get; set; }
        public bool? Clipped { get; set; }
        public bool? Collapsed { get; set; }
        public LayerLocks Locks { get; set; }
        public string? FilterSourceFile { get; set; }
        public SmartFilter[]? SmartFilters { get; set; }
        public int FilterPaddingX { get; set; }
        public int FilterPaddingY { get; set; }
        public string? SmartFilterMaskFile { get; set; }
        public bool SmartFilterMaskEnabled { get; set; } = true;
        public byte SmartFilterMaskDefault { get; set; } = 255;
        public double SmartFilterMaskX { get; set; }
        public double SmartFilterMaskY { get; set; }
        public VectorPath? VectorMask { get; set; }
        public bool? VectorMaskEnabled { get; set; }
        public bool VectorMaskFollowsWarp { get; set; } = true;
        public Adjustment? Adjustment { get; set; }
        public string? LookupFile { get; set; }
        public ShapeStyle? Shape { get; set; }
        public TextStyle? Text { get; set; }
        public LayerEffects? Effects { get; set; }
        public AutoLayout? AutoLayout { get; set; }
        public AutoLayoutItem? AutoLayoutItem { get; set; }
        public LayoutImageFrame? LayoutFrame { get; set; }
        public LayerStyleBinding? SharedTextStyle { get; set; }
        public LayerStyleBinding? SharedAppearanceStyle { get; set; }
        public List<LayerRecord>? Children { get; set; }
        public string? SmartObject { get; set; }
    }

    public static void Save(Document document, string path, Action? beforeReplace = null)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = File.Create(temp)) Write(document, stream);
            beforeReplace?.Invoke();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Write(Document document, Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Write(document, zip);
    }

    internal static void Write(Document document, ZipArchive zip)
    {
        var written = new Dictionary<SKBitmap, string>(ReferenceEqualityComparer.Instance);
        var sources = new Dictionary<SmartObjectSource, string>(ReferenceEqualityComparer.Instance);
        var objects = new Dictionary<string, SmartRecord>();
        var profiles = new Dictionary<RgbProfile, string>();
        var lookups = new Dictionary<string, string>(); long lookupBytes = 0;

        string StoreLookup(CubeLut table)
        {
            if (lookups.TryGetValue(table.Fingerprint, out var known)) return known;
            lookupBytes += table.AssetBytes;
            if (lookupBytes > DocumentLimits.MaxDocumentLutBytes) throw new InvalidDataException($"The project LUTs exceed {DocumentLimits.MaxDocumentLutBytes / 1024 / 1024} MB.");
            var name = lookups.Count + ".lut"; lookups.Add(table.Fingerprint, name);
            using var entry = zip.CreateEntry("lookups/" + name, CompressionLevel.Optimal).Open(); table.WriteBinary(entry); return name;
        }

        string? StoreProfile(RgbProfile profile)
        {
            if (profile.Equals(RgbProfile.Srgb)) return null;
            if (profiles.TryGetValue(profile, out var known)) return known;
            var name = profiles.Count + ".icc"; profiles.Add(profile, name);
            using var entry = zip.CreateEntry("profiles/" + name, CompressionLevel.Optimal).Open(); entry.Write(profile.Icc);
            return name;
        }

        string StoreSource(SmartObjectSource source)
        {
            if (sources.TryGetValue(source, out var existing)) return existing;
            var key = "object-" + sources.Count;
            sources.Add(source, key);
            var record = new SmartRecord { Id = source.Id, Link = source.Link }; objects.Add(key, record);
            record.Content = RecordDocument(source.OpenDocument());
            return key;
        }

        string Store(SKBitmap bitmap, string name)
        {
            if (written.TryGetValue(bitmap, out var existing)) return existing;
            var entry = zip.CreateEntry("images/" + name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            if (bitmap.ColorType == SKColorType.Alpha8)
            {
#if CANVAS_PERFORMANCE
                var encode = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                var bytes = EncodeMask(bitmap);
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.StageCompleted("CMPS encode raster PNG", encode);
                var write = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                output.Write(bytes);
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.StageCompleted("CMPS write raster entry", write);
#endif
            }
            else
            {
                // The encoder already owns the compressed PNG. Stream its chunks and profile
                // to the archive without building further copies on the large object heap.
#if CANVAS_PERFORMANCE
                var encode = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                using var pixels = bitmap.PeekPixels();
                using var encoded = pixels?.Encode(RasterPng) ?? throw new IOException("The image could not be encoded.");
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.StageCompleted("CMPS encode raster PNG", encode);
                var write = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                ImageProfiles.WritePng(output, encoded.AsSpan(), Pixels.ProfileOf(bitmap).IccBytes(), 72);
#if CANVAS_PERFORMANCE
                CanvasPerformanceTrace.StageCompleted("CMPS write raster entry", write);
#endif
            }
            return written[bitmap] = name;
        }

        LayerRecord Record(Layer layer) => new()
        {
            Id = layer.Id, Name = layer.Name, Tags = layer.Tags.Count > 0 ? layer.Tags.Order().ToList() : null,
            Kind = layer.Kind, Visible = layer.Visible, Opacity = layer.Opacity, FillOpacity = layer.FillOpacity, Blend = layer.Blend,
            BlendIf = layer.BlendIf.IsIdentity ? null : layer.BlendIf,
            Transform = layer.Pixels != null ? layer.Transform : null,
            Warp = layer.Warp,
            ImageFile = layer.Pixels != null && (!layer.IsSmartObject || layer.FilterSource != null || !ReferenceEquals(layer.Pixels, layer.SmartObject!.Preview)) ? Store(layer.Pixels, $"{written.Count}.png") : null,
            FilterSourceFile = layer.FilterSource != null ? Store(layer.FilterSource, $"{written.Count}.filter-source.png") : null,
            SmartFilters = layer.SmartFilters.Length == 0 ? null : layer.SmartFilters,
            FilterPaddingX = layer.FilterPaddingX, FilterPaddingY = layer.FilterPaddingY,
            SmartFilterMaskFile = layer.SmartFilterMask != null ? Store(layer.SmartFilterMask, $"{written.Count}.filter-mask.png") : null,
            SmartFilterMaskEnabled = layer.SmartFilterMaskEnabled, SmartFilterMaskDefault = layer.SmartFilterMaskDefault,
            SmartFilterMaskX = layer.SmartFilterMaskX, SmartFilterMaskY = layer.SmartFilterMaskY,
            VectorMask = layer.VectorMask, VectorMaskEnabled = layer.VectorMask == null ? null : layer.VectorMaskEnabled, VectorMaskFollowsWarp = layer.VectorMaskFollowsWarp,
            MaskFile = layer.Mask != null ? Store(layer.Mask, $"{written.Count}.mask.png") : null,
            MaskLinked = layer.MaskLinked, MaskFollowsWarp = layer.MaskFollowsWarp, MaskTransform = layer.MaskTransform, MaskDefault = layer.MaskDefault,
            MaskEnabled = layer.Mask != null ? layer.MaskEnabled : null, Locks = layer.Locks,
            MaskDensity = layer.MaskDensity, MaskFeather = layer.MaskFeather,
            Clipped = layer.Clipped ? true : null,
            Collapsed = layer.Collapsed ? true : null,
            Adjustment = layer.Adjustment is ColorLookupAdjustment lookup ? lookup with { Table = null } : layer.Adjustment,
            LookupFile = layer.Adjustment is ColorLookupAdjustment { Table: { } table } ? StoreLookup(table) : null,
            Shape = layer.Shape, Text = layer.Text, Effects = layer.Effects,
            AutoLayout = layer.AutoLayout, AutoLayoutItem = layer.AutoLayoutItem, LayoutFrame = layer.LayoutFrame,
            SharedTextStyle = layer.SharedTextStyle, SharedAppearanceStyle = layer.SharedAppearanceStyle,
            Children = layer.IsGroup ? layer.Children.Select(Record).ToList() : null,
            SmartObject = layer.SmartObject is { } source ? StoreSource(source) : null
        };

        Manifest RecordDocument(Document value) => new()
        {
            Width = value.Width, Height = value.Height, Resolution = value.Resolution,
            CanvasColorMode = value.ColorMode == CanvasColorMode.CMYK ? value.ColorMode : null,
            CanvasLayout = value.Layout != CanvasLayout.Empty ? value.Layout : null,
            BitDepth = value.WorkingDepth,
            ColorSpace = value.Profile.Name, ColorProfileFile = StoreProfile(value.Profile),
            ActiveLayerId = value.ActiveLayerId, Layers = value.Layers.Select(Record).ToList(),
            Styles = value.Styles.Count > 0 ? value.Styles.Values.ToList() : null,
            PromptRegions = value.PromptRegions.Count > 0 ? value.PromptRegions.ToList() : null,
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
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("CMPS open ZIP", started);
#endif
        return Read(zip, stream);
    }

    internal static Document Read(ZipArchive zip, Stream stream)
    {
#if CANVAS_PERFORMANCE
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("This is not a Composa project: it has no manifest.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("The project's manifest is too large.");
        Manifest manifest;
        using (var manifestStream = entry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(manifestStream, Json) ?? throw new InvalidDataException("The project's manifest is empty.");
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("CMPS load manifest", started);
#endif
        if (manifest.Format != Format) throw new InvalidDataException("This is not a Composa project.");
        if (manifest.Version > Version) throw new InvalidDataException($"This project uses format version {manifest.Version}; this app supports up to version {Version}.");
        if (manifest.Width < 1 || manifest.Height < 1 || manifest.Width > Document.MaxSide || manifest.Height > Document.MaxSide)
            throw new InvalidDataException("The project's canvas size is invalid.");

        var cache = new Dictionary<string, SKBitmap>();
        var sources = new Dictionary<string, SmartObjectSource>();
        var visiting = new HashSet<string>();
        var profiles = new Dictionary<(string File, string Name), RgbProfile>();
        var lookups = new Dictionary<string, CubeLut>(); long lookupBytes = 0;
        long allocatedPixels = 0;
        var count = 0;

        CubeLut FetchLookup(string name)
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("The project refers to an unsafe LUT path.");
            if (lookups.TryGetValue(name, out var known)) return known;
            var entry = zip.GetEntry("lookups/" + name) ?? throw new InvalidDataException("The project lookup table is missing.");
            if (entry.Length < 1 || entry.Length > DocumentLimits.MaxLutBytes + DocumentLimits.MaxLutLineLength + 128 || lookupBytes + entry.Length > DocumentLimits.MaxDocumentLutBytes)
                throw new InvalidDataException("The project LUTs exceed the supported memory budget.");
            using var input = entry.Open(); var table = CubeLut.ReadBinary(input); lookupBytes += entry.Length; lookups.Add(name, table); return table;
        }

        RgbProfile FetchProfile(string? name, string title)
        {
            if (name == null) return RgbProfile.Srgb;
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("The project refers to an unsafe profile path.");
            if (profiles.TryGetValue((name, title), out var known)) return known;
            var entry = zip.GetEntry("profiles/" + name) ?? throw new InvalidDataException("The project color profile is missing.");
            if (entry.Length < 132 || entry.Length > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The project color profile has an invalid size.");
            var bytes = new byte[entry.Length]; using (var input = entry.Open()) input.ReadExactly(bytes);
            return profiles[(name, title)] = RgbProfile.FromIcc(bytes, title);
        }

        SKBitmap Fetch(string name, bool mask)
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("The project refers to an unsafe path.");
            if (cache.TryGetValue(name, out var cached)) return cached;
            var image = zip.GetEntry("images/" + name) ?? throw new InvalidDataException($"An image inside the project is missing ({name}).");
            var useNativeBuffer = !mask && stream.CanSeek && image.Length >= 8 * 1024 * 1024
                && image.Length <= Math.Min(DocumentLimits.MaxSurfaceBytes, int.MaxValue)
                && image.CompressedLength == image.Length && image.Length <= stream.Length;
            var bitmap = ReadRaster(image, name, mask, useNativeBuffer);
            allocatedPixels += ((long)bitmap.ByteCount + 3) / 4;
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
#if CANVAS_PERFORMANCE
            var previewStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            var source = SmartObjectSource.Create(value, record.Id == Guid.Empty ? null : record.Id, record.Link);
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.StageCompleted("CMPS materialize embedded preview", previewStart);
#endif
            sources.Add(key, source); visiting.Remove(key);
            allocatedPixels += ((long)source.Preview.ByteCount + 3) / 4;
            if (allocatedPixels > DocumentLimits.DocumentPixelBudget) throw new InvalidDataException("The project exceeds the document raster budget.");
            return source;
        }

        Layer Build(LayerRecord record, int depth, int version)
        {
            if (++count > 10_000 || depth > 64) throw new InvalidDataException("The project has too many layers.");
            var adjustment = record.Adjustment;
            if (adjustment is ColorLookupAdjustment lookup)
            {
                if (lookup.Table != null) throw new InvalidDataException("Project lookup samples must be stored as an embedded asset.");
                adjustment = lookup with { Table = record.LookupFile == null ? null : FetchLookup(record.LookupFile), Strength = double.IsFinite(lookup.Strength) ? Math.Clamp(lookup.Strength, 0, 100) : 0,
                    Space = Enum.IsDefined(lookup.Space) ? lookup.Space : LookupRgbSpace.Srgb };
            }
            else if (record.LookupFile != null) throw new InvalidDataException("A LUT asset requires a Color Lookup adjustment.");
            var layer = new Layer
            {
                Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id, Name = record.Name, Kind = record.Kind, Visible = record.Visible,
                Opacity = double.IsFinite(record.Opacity) ? Math.Clamp(record.Opacity, 0, 1) : 1, FillOpacity = double.IsFinite(record.FillOpacity) ? Math.Clamp(record.FillOpacity, 0, 1) : 1,
                Blend = version <= 10 && record.Kind == LayerKind.Group && record.Blend == BlendMode.Normal ? BlendMode.PassThrough : record.Blend,
                BlendIf = record.BlendIf?.Normalized() ?? new(),
                MaskEnabled = record.MaskEnabled ?? true, Clipped = record.Clipped ?? false, Collapsed = record.Collapsed ?? false, Locks = record.Locks & LayerLocks.All,
                MaskDensity = double.IsFinite(record.MaskDensity) ? Math.Clamp(record.MaskDensity, 0, 1) : 1,
                MaskFeather = double.IsFinite(record.MaskFeather) ? Math.Clamp(record.MaskFeather, 0, 1000) : 0,
                Adjustment = adjustment, Shape = record.Shape?.Clamped(), Text = record.Text?.Clamped(),
                AutoLayout = record.Kind == LayerKind.Group ? record.AutoLayout?.Normalized() : null,
                AutoLayoutItem = record.Kind == LayerKind.Raster ? record.AutoLayoutItem?.Normalized() : null,
                LayoutFrame = record.Kind == LayerKind.Raster && record.LayoutFrame is { IsValid: true } frame ? frame : null,
                SharedTextStyle = record.SharedTextStyle, SharedAppearanceStyle = record.SharedAppearanceStyle,
                VectorMask = record.VectorMask?.Normalized(), VectorMaskEnabled = record.VectorMaskEnabled ?? true, VectorMaskFollowsWarp = record.VectorMaskFollowsWarp
            };
            if (record.Tags != null)
                foreach (var tag in record.Tags.Take(64)) if (LayerTags.Normalize(tag) is { } normalized) layer.Tags.Add(normalized);
            if (record.SmartObject != null)
            {
                if (record.Kind != LayerKind.Raster || record.Text != null || record.Shape != null)
                    throw new InvalidDataException("A smart object cannot also be a group, adjustment, text or shape layer.");
                layer.SmartObject = Source(record.SmartObject);
                layer.Pixels = record.ImageFile == null ? layer.SmartObject.Preview : Fetch(record.ImageFile, mask: false);
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
            layer.MaskLinked = record.MaskLinked;
            layer.MaskFollowsWarp = record.MaskFollowsWarp;
            layer.MaskDefault = record.MaskDefault;
            layer.MaskTransform = IsUsableMaskTransform(record.MaskTransform) ? record.MaskTransform : null;
            if (layer.Mask != null && !layer.MaskLinked && layer.MaskTransform == null) layer.PlaceMask(layer.MaskMatrix);
            if (record.FilterSourceFile != null)
            {
                layer.FilterSource = Fetch(record.FilterSourceFile, mask: false);
                layer.SmartFilters = record.SmartFilters?.Take(64).Where(f => Enum.IsDefined(f.Settings.Kind)).Select(f => f.Normalized()).ToArray() ?? [];
                layer.FilterPaddingX = Math.Clamp(record.FilterPaddingX, 0, DocumentLimits.MaxSide);
                layer.FilterPaddingY = Math.Clamp(record.FilterPaddingY, 0, DocumentLimits.MaxSide);
                if (record.SmartFilterMaskFile != null) layer.SmartFilterMask = Fetch(record.SmartFilterMaskFile, mask: true);
                layer.SmartFilterMaskEnabled = record.SmartFilterMaskEnabled; layer.SmartFilterMaskDefault = record.SmartFilterMaskDefault;
                layer.SmartFilterMaskX = double.IsFinite(record.SmartFilterMaskX) ? Math.Clamp(record.SmartFilterMaskX, -DocumentLimits.MaxSide, DocumentLimits.MaxSide) : 0;
                layer.SmartFilterMaskY = double.IsFinite(record.SmartFilterMaskY) ? Math.Clamp(record.SmartFilterMaskY, -DocumentLimits.MaxSide, DocumentLimits.MaxSide) : 0;
                if (record.ImageFile != null) layer.Pixels = Fetch(record.ImageFile, mask: false);
            }
            if (record.Kind == LayerKind.Group && record.Children != null)
                foreach (var child in record.Children) layer.Children.Add(Build(child, depth + 1, version));
            if (record.Warp != null)
            {
                if (layer.Pixels == null) throw new InvalidDataException("A Warp surface requires a pixel layer.");
                var bounds = Geometry.RoundOut(layer.GeometryForWarp(record.Warp).Bounds);
                if (!DocumentLimits.FitsSurface(bounds.Width, bounds.Height, layer.Pixels.BytesPerPixel)) throw new InvalidDataException("The Warp surface exceeds supported dimensions or memory.");
                layer.Warp = record.Warp;
            }
            return layer;
        }

        Document BuildDocument(Manifest value)
        {
            if (!DocumentLimits.FitsSurface(value.Width, value.Height) || value.Format != Format || value.Version > Version)
                throw new InvalidDataException("Invalid embedded document size or format.");
            var result = new Document(value.Width, value.Height) { Resolution = double.IsFinite(value.Resolution) ? Math.Clamp(value.Resolution, 1, 9600) : 72,
                ColorMode = value.CanvasColorMode == CanvasColorMode.CMYK ? CanvasColorMode.CMYK : CanvasColorMode.RGB,
                Layout = value.CanvasLayout?.Normalized() ?? CanvasLayout.Empty,
                Depth = value.BitDepth == ColorDepth.Sixteen ? ColorDepth.Sixteen : ColorDepth.Eight,
                Profile = FetchProfile(value.ColorProfileFile, value.ColorSpace) };
            foreach (var record in value.Layers) result.Layers.Add(Build(record, 0, value.Version));
            if (value.Styles is { } styles)
            {
                if (styles.Count > DocumentLimits.MaxLayerStyles) throw new InvalidDataException("The project has too many layer styles.");
                var names = new HashSet<(LayerStyleKind, string)>();
                foreach (var entry in styles)
                    if (entry?.Normalized() is { } style && !result.Styles.ContainsKey(style.Id) && names.Add((style.Kind, style.Name.ToUpperInvariant())))
                        result.Styles.Add(style.Id, style);
            }
            if (value.Version >= 18)
                foreach (var layer in result.AllLayers())
                    foreach (var kind in Enum.GetValues<LayerStyleKind>())
                    {
                        if (layer.StyleBinding(kind) is not { } binding) continue;
                        if (!result.Styles.TryGetValue(binding.Id, out var style) || style.Kind != kind
                            || !Composa.Editing.EditorSession.SupportsStyle(layer, kind)) layer.SetStyleBinding(kind, null);
                        else layer.SetStyleBinding(kind, binding with { Overrides = binding.Overrides & style.Fields });
                    }
            if (value.AlphaChannels != null)
                foreach (var pair in value.AlphaChannels.Take(64))
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 200) result.AlphaChannels[pair.Key] = Fetch(pair.Value, mask: true);
            if (value.PromptRegions != null)
                result.PromptRegions.AddRange(value.PromptRegions.Where(r => r is { Valid: true }).DistinctBy(r => r.Number).Take(1000));
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

    private static unsafe SKBitmap ReadRaster(ZipArchiveEntry entry, string name, bool mask, bool useNativeBuffer)
    {
#if CANVAS_PERFORMANCE
        var extractStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        SKBitmap Decode(Stream encoded)
        {
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.StageCompleted("CMPS extract raster entry", extractStart);
            var decodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            var decoded = mask ? DecodeMask(encoded)
                : Png16Codec.TryDecode(encoded, name, preserveProfile: true) ?? ImageFiles.Load(encoded, name, preserveProfile: true);
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.StageCompleted("CMPS decode raster PNG", decodeStart);
#endif
            return decoded;
        }

        using var input = entry.Open();
        if (!useNativeBuffer)
        {
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            buffer.Position = 0;
            return Decode(buffer);
        }

        // The project stores PNG entries without ZIP compression. Use a seekable buffer
        // with a bounded lifetime for a large entry instead of growing arrays on the LOH.
        var length = (int)entry.Length;
        var memory = (byte*)NativeMemory.Alloc((nuint)length);
        try
        {
            input.ReadExactly(new Span<byte>(memory, length));
            if (input.ReadByte() != -1) throw new InvalidDataException("The project image exceeds its recorded size.");
            using var buffer = new UnmanagedMemoryStream(memory, length);
            return Decode(buffer);
        }
        finally { NativeMemory.Free(memory); }
    }

    /// <summary>A damaged file must not feed non-finite or degenerate placements into rendering.</summary>
    private static bool IsUsable(LayerTransform? t) =>
        t != null && new[] { t.X, t.Y, t.Width, t.Height, t.Rotation }.All(double.IsFinite)
        && t.Width >= 1 && t.Height >= 1 && t.Width <= 1_000_000 && t.Height <= 1_000_000 && Math.Abs(t.X) <= 10_000_000 && Math.Abs(t.Y) <= 10_000_000
        && (t.Distort == null || (t.Distort.Length == 8 && t.Distort.All(float.IsFinite)));

    private static bool IsUsableMaskTransform(LayerTransform? t) => t != null && IsUsable(t with { Width = Math.Max(1, t.Width), Height = Math.Max(1, t.Height) })
        && t.Width > 1e-10 && t.Height > 1e-10;

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
        var mask = Pixels.NewLayerMask(gray.Width, gray.Height);
        CopyRows(gray, mask);
        return mask;
    }

    private static unsafe void CopyRows(SKBitmap from, SKBitmap to)
    {
        for (var y = 0; y < from.Height; y++)
            Buffer.MemoryCopy((byte*)from.GetPixels() + (long)y * from.RowBytes, (byte*)to.GetPixels() + (long)y * to.RowBytes, to.RowBytes, from.Width);
    }
}

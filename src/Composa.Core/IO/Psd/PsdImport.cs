using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// A Photoshop file rebuilt as this editor's layers, as far as they can carry it, with a report of everything that had
/// to be converted on the way. Nothing is applied to a document until the caller decides to, so the report can be
/// shown first. Photoshop is an interchange format here: files are opened with a conversion report and written by PsdExport.
/// </summary>
public sealed class PsdImport
{
    public int Width { get; }
    public int Height { get; }
    public double Resolution { get; }
    public IReadOnlyList<Guide> Guides { get; private init; } = [];
    /// <summary>Root layers, bottom to top, with folders holding their children. Meant to be placed once.</summary>
    public List<Layer> Layers { get; }
    public IReadOnlyList<PsdConversion> Conversions { get; }

    private PsdImport(int width, int height, double resolution, List<Layer> layers, List<PsdConversion> conversions)
    {
        Width = width;
        Height = height;
        Resolution = resolution;
        Layers = layers;
        Conversions = conversions;
    }

    /// <summary>Photoshop files are known by their <c>8BPS</c> signature, whatever their extension.</summary>
    public static bool IsPsd(string path) => PsdReader.Matches(path);
    public static bool IsPsd(ReadOnlySpan<byte> data) => PsdReader.Matches(data);

    /// <summary>Reads a file that is to become a document of its own, so the whole document budget is its to use.</summary>
    public static PsdImport Load(string path) => Load(path, DocumentLimits.DocumentPixelBudget);
    public static PsdImport Load(byte[] data) => Load(data, DocumentLimits.DocumentPixelBudget);

    /// <param name="pixelBudget">How much raster the file may add: the document budget, less what the target document already holds.</param>
    public static PsdImport Load(string path, long pixelBudget)
    {
        var info = new FileInfo(path);
        if (info.Length > int.MaxValue) throw PsdException.TooLarge();
        return Load(File.ReadAllBytes(path), pixelBudget);
    }

    public static PsdImport Load(byte[] data, long pixelBudget)
        => LoadNested(data, pixelBudget, 0);

    private static PsdImport LoadNested(byte[] data, long pixelBudget, int depth)
    {
        if (depth >= SmartObjectSource.MaxDepth) throw new PsdException("The Photoshop file contains too many nested smart objects.");
        var file = PsdReader.Read(data, pixelBudget);
        try
        {
            var import = Build(file, pixelBudget, depth);
            if (import.ToDocument().RasterPixels() > pixelBudget) { import.Discard(); throw PsdException.TooLarge(); }
            return import;
        }
        catch
        {
            foreach (var layer in file.Layers) { layer.Image?.Dispose(); layer.MaskImage?.Dispose(); }
            file.Composite?.Dispose();
            throw;
        }
    }

    /// <summary>The Photoshop-authored merged appearance as one layer, without reconstructing unsupported features.</summary>
    public static PsdImport LoadAppearance(string path, long pixelBudget)
    {
        if (new FileInfo(path).Length > int.MaxValue) throw PsdException.TooLarge();
        return LoadAppearance(File.ReadAllBytes(path), pixelBudget);
    }

    public static PsdImport LoadAppearance(byte[] data, long pixelBudget)
    {
        var file = PsdReader.Read(data, pixelBudget, mergedOnly: true);
        var import = Build(file, pixelBudget);
        return new PsdImport(import.Width, import.Height, import.Resolution, import.Layers,
            [.. import.Conversions, new PsdConversion("Document", "Opened Photoshop's merged appearance as one layer. The original Photoshop layers were not imported.")]) { Guides = import.Guides };
    }

    /// <summary>Frees the layers' pixels when the import is not going ahead.</summary>
    public void Discard()
    {
        var bitmaps = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        ToDocument().CollectBitmaps(bitmaps, new(), includeSelection: false);
        foreach (var bitmap in bitmaps) bitmap.Dispose();
        foreach (var layer in Document.Flatten(Layers))
        {
            layer.Pixels = null;
            layer.Mask = null;
        }
        Layers.Clear();
    }

    /// <summary>A new document holding the layers, the topmost root layer active.</summary>
    public Document ToDocument()
    {
        var document = new Document(Width, Height) { Resolution = Resolution };
        document.Layers.AddRange(Layers);
        document.Guides.AddRange(Guides);
        document.SetActive(Layers.LastOrDefault()?.Id);
        return document;
    }

    private static readonly Dictionary<string, BlendMode> Blends = new()
    {
        ["norm"] = BlendMode.Normal, ["mul "] = BlendMode.Multiply, ["scrn"] = BlendMode.Screen, ["over"] = BlendMode.Overlay,
        ["dark"] = BlendMode.Darken, ["lite"] = BlendMode.Lighten, ["diff"] = BlendMode.Difference, ["div "] = BlendMode.ColorDodge,
        ["idiv"] = BlendMode.ColorBurn, ["sLit"] = BlendMode.SoftLight, ["hLit"] = BlendMode.HardLight, ["smud"] = BlendMode.Exclusion,
        ["hue "] = BlendMode.Hue, ["sat "] = BlendMode.Saturation, ["colr"] = BlendMode.Color, ["lum "] = BlendMode.Luminosity,
        ["lbrn"] = BlendMode.LinearBurn, ["lddg"] = BlendMode.LinearDodge, ["vLit"] = BlendMode.VividLight, ["lLit"] = BlendMode.LinearLight,
        ["pLit"] = BlendMode.PinLight, ["hMix"] = BlendMode.HardMix, ["fsub"] = BlendMode.Subtract, ["fdiv"] = BlendMode.Divide
        // Dissolve, Darker Color and Lighter Color have no equivalent here and fall through to Normal with a conversion listed.
    };

    internal static string BlendKey(BlendMode mode) => Blends.First(pair => pair.Value == mode).Key;

    /// <summary>Listed for every layer the reader had to cut to the canvas to make the file fit.</summary>
    public const string CroppedNote = "Cropped to the canvas so the file fits in memory. Pixels outside the canvas weren't imported.";

    private static readonly string[] TextKeys = ["TySh", "tySh", "txt2"];
    private static readonly string[] VectorKeys = ["vmsk", "vsms", "vogk"];
    private static readonly string[] SmartObjectKeys = ["SoLd", "SoLE", "PlLd", "plLd"];
    private static readonly string[] EffectKeys = ["lfx2", "lrFX", "lmfx"];
    private static readonly string[] OtherFillKeys = ["GdFl", "PtFl"];

    private static PsdImport Build(PsdFile file, long pixelBudget, int depth = 0)
    {
        var conversions = new List<PsdConversion>();
        if (file.Depth == 16) conversions.Add(new PsdConversion("Document", "16-bit RGB channels are converted to the editor's 8-bit channels. Save the original PSD to retain its full precision."));
        var canvas = new SKSizeI(file.Width, file.Height);
        var remaining = pixelBudget - file.Layers.Sum(l => (long)(l.Image?.Width ?? 0) * (l.Image?.Height ?? 0));
        var embedded = PsdSmartObjects.Embedded(file);
        var sources = new Dictionary<string, SmartObjectSource>(StringComparer.Ordinal);

        if (file.Layers.Count == 0)
        {
            // A flattened file: the merged image is all there is, and it becomes the one layer.
            var flat = new List<Layer>();
            if (file.Composite != null) flat.Add(Layer.Raster("Background", file.Composite));
            return Complete(file, flat, conversions);
        }

        // Photoshop lists layers bottom to top: a folder's hidden divider comes first, then its contents, then the
        // folder record itself. Children are collected under the divider's id until their folder arrives.
        var roots = new List<Layer>();
        var pending = new Dictionary<Guid, List<Layer>>();
        var openGroups = new Stack<Guid>();
        var clipping = new HashSet<Guid>();
        foreach (var record in file.Layers)
        {
            if (record.IsDivider)
            {
                var id = Guid.NewGuid();
                openGroups.Push(id);
                pending[id] = [];
                record.Image?.Dispose();
                record.MaskImage?.Dispose();
                continue;
            }
            var name = record.Name.Length == 0 ? "Layer" : record.Name;
            if (record.Cropped) conversions.Add(new PsdConversion(name, CroppedNote));
            var target = openGroups.Count > 0 ? pending[openGroups.Peek()] : roots;
            Layer? layer;
            if (record.IsGroup)
            {
                var id = openGroups.Count > 0 ? openGroups.Pop() : Guid.NewGuid();
                layer = new Layer { Id = id, Name = name, Kind = LayerKind.Group, Collapsed = record.Section == 2 };
                if (pending.TryGetValue(id, out var children)) layer.Children.AddRange(children);
                target = openGroups.Count > 0 ? pending[openGroups.Peek()] : roots;
                if (record.BlendKey != "pass" && !Blends.ContainsKey(record.BlendKey)) conversions.Add(new PsdConversion(name, $"Folder blend mode \"{record.BlendKey.Trim()}\" isn't supported. The folder will be pass-through."));
                layer.Blend = Blends.GetValueOrDefault(record.BlendKey, BlendMode.Normal);
                record.Image?.Dispose();
            }
            else
            {
                layer = BuildLayer(record, name, canvas, ref remaining, conversions);
                if (SmartObjectKeys.Any(record.Extra.ContainsKey))
                    layer = ImportSmartObject(record, layer, name, embedded, sources, ref remaining, depth, conversions);
                if (layer == null) continue;
                if (!Blends.ContainsKey(record.BlendKey) && record.BlendKey != "pass")
                    conversions.Add(new PsdConversion(name, $"Blend mode \"{record.BlendKey.Trim()}\" isn't supported and will be applied as Normal."));
                layer.Blend = Blends.GetValueOrDefault(record.BlendKey, BlendMode.Normal);
                if (record.Clipping) clipping.Add(layer.Id);
            }
            layer.Visible = !record.Hidden;
            if (layer.Pixels != null) layer.Effects = PsdEffects.Read(record, file.GlobalLightAngle, conversions);
            else if (EffectKeys.Any(record.Extra.ContainsKey)) conversions.Add(new(name, "Effects on folders/adjustments are not supported."));
            layer.Opacity = Opacity(record);
            if (EffectKeys.Any(record.Extra.ContainsKey)) layer.FillOpacity = record.Fill / 255.0;
            ApplyMask(record, layer, canvas, conversions);
            target.Add(layer);
        }
        // A file whose folders never closed is damaged: keep their contents at the top level rather than lose them.
        while (openGroups.Count > 0) roots.AddRange(pending[openGroups.Pop()]);
        ResolveClipping(roots, clipping, conversions);
        return Complete(file, roots, conversions);
    }

    private static PsdImport Complete(PsdFile file, List<Layer> layers, List<PsdConversion> conversions)
    {
        var import = new PsdImport(file.Width, file.Height, file.Resolution, layers, conversions) { Guides = file.Guides };
        try { PsdColor.Apply(layers, file.ColorProfile, conversions); return import; }
        catch { import.Discard(); throw; }
    }

    /// <summary>Layer opacity times fill opacity, as Photoshop shows them multiplied; a layer with effects keeps fill for the effects, which are dropped here.</summary>
    private static double Opacity(PsdLayer record)
    {
        var hasEffects = EffectKeys.Any(record.Extra.ContainsKey);
        var opacity = record.Opacity / 255.0;
        return Math.Clamp(hasEffects && record.Fill != 255 ? opacity : opacity * (record.Fill / 255.0), 0, 1);
    }

    private static Layer? BuildLayer(PsdLayer record, string name, SKSizeI canvas, ref long remaining, List<PsdConversion> conversions)
    {
        var extra = record.Extra;
        void Note(string message) => conversions.Add(new PsdConversion(name, message));
        var kind = SmartObjectKeys.Any(extra.ContainsKey) ? PsdLayerKind.SmartObject
            : TextKeys.Any(extra.ContainsKey) ? PsdLayerKind.Text
            : VectorKeys.Any(extra.ContainsKey) ? PsdLayerKind.Vector
            : PsdAdjustments.IsAdjustment(extra) ? PsdLayerKind.Adjustment
            : extra.ContainsKey("SoCo") ? PsdLayerKind.Fill
            : OtherFillKeys.Any(extra.ContainsKey) ? PsdLayerKind.Other
            : PsdLayerKind.Raster;

        if (kind == PsdLayerKind.Adjustment)
        {
            record.Image?.Dispose();
            var adjustment = PsdAdjustments.Parse(extra, out var approximate);
            if (adjustment == null) { Note("This adjustment type isn't supported and was skipped."); record.MaskImage?.Dispose(); return null; }
            if (approximate) Note("Adjustment parameters may not match Photoshop exactly.");
            var layer = Layer.ForAdjustment(adjustment);
            layer.Name = name;
            return layer;
        }
        if (kind == PsdLayerKind.Text)
        {
            // Horizontal type keeps its wording, font, size, color, alignment, tracking and leading, so it can be retyped.
            var available = remaining + (long)(record.Image?.Width ?? 0) * (record.Image?.Height ?? 0);
            if (PsdText.Parse(extra) is { } source && PsdText.Place(source, name, ref available) is { } text)
            {
                remaining = available;
                record.Image?.Dispose();
                foreach (var note in source.Notes) Note(note);
                return text;
            }
            Note(PsdText.RasterizedNote);
        }
        if (kind == PsdLayerKind.Other) Note("Gradient and pattern fills aren't supported; the layer was imported empty.");

        if (kind is PsdLayerKind.Vector or PsdLayerKind.Fill)
        {
            var cached = (long)(record.Image?.Width ?? 0) * (record.Image?.Height ?? 0);
            if (PsdVector.LiveShape(extra, canvas, remaining + cached) is { } live)
            {
                record.Image?.Dispose();
                foreach (var note in live.Notes) Note(note);
                remaining += cached - (long)live.Bounds.Width * live.Bounds.Height;
                var pixels = EditorSession.RenderShape(live.Style, live.Bounds.Width, live.Bounds.Height);
                var shape = AsShape(Layer.Raster(name, pixels, live.Bounds.Left, live.Bounds.Top), live.Style);
                if (live.Placement is { } matrix) shape.Transform = PsdGeometry.Place(matrix, pixels.Width, pixels.Height);
                return shape;
            }
            if (kind == PsdLayerKind.Fill && PsdVector.FillColor(extra) is { } fill)
            {
                // A solid color fill with no path covers the whole canvas.
                record.Image?.Dispose();
                var style = new ShapeStyle(ShapeKind.Rectangle, fill, 0);
                return AsShape(Layer.Raster(name, EditorSession.RenderShape(style, canvas.Width, canvas.Height)), style);
            }
            if (record.Image == null && PsdVector.Rasterized(extra, canvas, remaining) is { } raster)
            {
                Note("Vector shape was rasterized to pixels.");
                remaining -= (long)raster.Bounds.Width * raster.Bounds.Height;
                return Layer.Raster(name, raster.Image, raster.Bounds.Left, raster.Bounds.Top);
            }
            if (kind == PsdLayerKind.Vector) Note("Vector shape was rasterized to pixels.");
        }
        if (record.Image is { } image) return Layer.Raster(name, image, record.Left, record.Top);
        // Cropping can remove the whole layer. Keep one transparent pixel at its original location,
        // rather than allocating a canvas-sized placeholder that defeats the import budget.
        if (record.Cropped) return Layer.Raster(name, Pixels.NewColor(1, 1), record.Left, record.Top);
        // No pixel area: an empty layer over the canvas, as Photoshop shows it.
        return Layer.Raster(name, Pixels.NewColor(canvas.Width, canvas.Height));
    }

    /// <summary>The layer as a live shape: redrawn sharp whenever it is scaled.</summary>
    private static Layer? ImportSmartObject(PsdLayer record, Layer? fallback, string name, Dictionary<string, byte[]> embedded,
        Dictionary<string, SmartObjectSource> sources, ref long remaining, int depth, List<PsdConversion> conversions)
    {
        void Note(string message) => conversions.Add(new(name, message));
        var placement = PsdSmartObjects.Placement(record);
        if (placement.Warped || record.Extra.ContainsKey("FXid") || record.Extra.ContainsKey("FEid") || record.Extra.ContainsKey("vmsk") || record.Extra.ContainsKey("vsms"))
        { Note("The smart object's warp, vector mask or smart filters are preserved in its Photoshop compatibility pixels; its source cannot be edited here."); return fallback; }
        if (placement.Id == null || placement.Quad == null || !embedded.TryGetValue(placement.Id, out var data))
        { Note("The smart object has no usable embedded content/placement (it may be externally linked). Photoshop compatibility pixels were kept."); return fallback; }
        var cache = (long)(record.Image?.Width ?? 0) * (record.Image?.Height ?? 0);
        var initialRemaining = remaining;
        var available = remaining + cache;
        PsdImport? inner = null;
        SmartObjectSource? created = null;
        Document? createdContents = null;
        try
        {
            if (!sources.TryGetValue(placement.Id, out var source))
            {
                Document contents;
                if (PsdReader.Matches(data))
                {
                    inner = LoadNested(data, available, depth + 1); contents = inner.ToDocument();
                    foreach (var conversion in inner.Conversions) Note("Embedded content: " + conversion.Message);
                }
                else
                {
                    using var stream = new MemoryStream(data, writable: false);
                    var image = ImageFiles.Load(stream, "Embedded smart object");
                    if ((long)image.Width * image.Height * 2 > available) { image.Dispose(); throw PsdException.TooLarge(); }
                    contents = new Document(image.Width, image.Height); contents.Layers.Add(Layer.Raster("Contents", image));
                }
                if (contents.RasterPixels() + (long)contents.Width * contents.Height > available) throw PsdException.TooLarge();
                createdContents = contents;
                source = created = SmartObjectSource.Create(contents);
                var cost = contents.RasterPixels() + (long)source.Width * source.Height;
                remaining = available - cost;
            }
            else remaining += cache;
            var transform = PsdSmartObjects.Place(placement.Quad, source.Width, source.Height);
            sources.TryAdd(placement.Id, source);
            if (fallback?.Pixels != null && !ReferenceEquals(fallback.Pixels, record.Image)) fallback.Pixels.Dispose();
            record.Image?.Dispose(); record.Image = null;
            var layer = Layer.Raster(name, source.Preview); layer.SmartObject = source; layer.Transform = transform;
            return layer;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException)
        {
            remaining = initialRemaining;
            created?.Preview.Dispose();
            if (createdContents != null && inner == null)
            {
                var bitmaps = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
                createdContents.CollectBitmaps(bitmaps, new(), includeSelection: false);
                foreach (var bitmap in bitmaps) bitmap.Dispose();
            }
            inner?.Discard();
            Note("The smart object's embedded content could not be rebuilt; compatibility pixels were kept. " + error.Message);
            return fallback;
        }
    }

    /// <summary>The layer as a live shape: redrawn sharp whenever it is scaled.</summary>
    private static Layer AsShape(Layer layer, ShapeStyle style)
    {
        layer.Shape = style;
        return layer;
    }

    /// <summary>
    /// Photoshop keeps a mask over its own rectangle with a default value outside it; here a mask shares its layer's
    /// pixel grid (or the document's, for folders and adjustments), so the plane is placed into one of that size.
    /// </summary>
    private static void ApplyMask(PsdLayer record, Layer layer, SKSizeI canvas, List<PsdConversion> conversions)
    {
        if (!record.HasMask || record.MaskFromRender) { record.MaskImage?.Dispose(); return; }
        int width, height, dx, dy;
        if (layer.Pixels is { } pixels)
        {
            width = pixels.Width; height = pixels.Height;
            dx = record.MaskLeft - (int)Math.Round(layer.Transform.X); dy = record.MaskTop - (int)Math.Round(layer.Transform.Y);
        }
        else
        {
            width = canvas.Width; height = canvas.Height;
            dx = record.MaskLeft; dy = record.MaskTop;
        }
        var mask = Pixels.NewMask(width, height, record.MaskDefault);
        if (record.MaskImage is { } plane)
        {
            using (plane)
            {
                if ((layer.IsLive || layer.IsSmartObject) && layer.Pixels is { } source && !layer.Transform.IsPureTranslation(source.Width, source.Height) && layer.Matrix.TryInvert(out var inverse))
                {
                    // PSD masks are in document coordinates. Move coverage back into the live layer's local grid.
                    using var surface = new SKCanvas(mask);
                    surface.SetMatrix(SKMatrix.CreateTranslation(record.MaskLeft, record.MaskTop).PostConcat(inverse));
                    using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                    using var image = SKImage.FromBitmap(plane);
                    surface.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
                }
                else
                {
                    using var surface = new SKCanvas(mask);
                    using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                    surface.DrawBitmap(plane, dx, dy, paint);
                }
            }
            Pixels.Invalidate(mask);
        }
        layer.Mask = mask;
        layer.MaskEnabled = !record.MaskDisabled;
        if (!record.MaskLinked) conversions.Add(new PsdConversion(layer.Name, "The mask was unlinked from its layer in Photoshop; here it moves with the layer."));
    }

    /// <summary>A clipped layer follows the sibling below it here, so clipping onto a folder or an adjustment has no base to keep.</summary>
    private static void ResolveClipping(List<Layer> siblings, HashSet<Guid> clipping, List<PsdConversion> conversions)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            var layer = siblings[i];
            if (layer.IsGroup) ResolveClipping(layer.Children, clipping, conversions);
            if (!clipping.Contains(layer.Id)) continue;
            Layer? baseLayer = null;
            for (var j = i - 1; j >= 0; j--)
                if (!clipping.Contains(siblings[j].Id)) { baseLayer = siblings[j]; break; }
            if (baseLayer is { Pixels: not null }) layer.Clipped = true;
            else conversions.Add(new PsdConversion(layer.Name, "This clipping mask's base isn't supported, so clipping was skipped."));
        }
    }
}

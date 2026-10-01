using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.AI;

public enum RemoveObjectFillMode { Neutral, Solid, Transparent }

public sealed record RemoveObjectSettings
{
    public int Dilation { get; init; } = 8;
    public float Feather { get; init; } = 4;
    public RemoveObjectFillMode FillMode { get; init; } = RemoveObjectFillMode.Solid;
    public SKColor FillColor { get; init; } = SKColors.Black;
}

public sealed record AiGenerationSettings
{
    public int Variants { get; init; } = 1;
    public AiVariantMode VariantMode { get; init; } = AiVariantMode.List;
    public int UpscaleFactor { get; init; } = 4;
    public int Width { get; init; }
    public int Height { get; init; }
    public long Seed { get; init; } = -1;
    public IReadOnlyList<AiLora> Loras { get; init; } = [];
    public Dictionary<string, object?> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record AiLora(string Name, double Strength = 1, bool Enabled = true);

public enum AiVariantMode { List, Batch }
public enum AiExpansionMode { MaskedRegion, WholeImage }

public sealed record AiTaskRequest
{
    public AiTaskKind Task { get; init; }
    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public string AdditionalPrompt { get; init; } = "";
    public AiGenerationSettings Settings { get; init; } = new();
    public SKRectI? ExpansionBounds { get; init; }
    public AiExpansionMode ExpansionMode { get; init; } = AiExpansionMode.MaskedRegion;
    /// <summary>Zero preserves the expanded canvas dimensions; otherwise the generation's minimum side.</summary>
    public int ExpansionMinimumSide { get; init; } = 1024;
    public RemoveObjectSettings RemoveObject { get; init; } = new();
    /// <summary>An optional user-supplied visual reference. Kept for Engine Pack backwards compatibility.</summary>
    public SKBitmap? ReferenceImage { get; init; }
    /// <summary>User-supplied visual references, in UI order. Engine Packs bind them as referenceImage1…referenceImage6.</summary>
    public IReadOnlyList<SKBitmap> ReferenceImages { get; init; } = [];
    /// <summary>Pixel budget for each reference, or null to preserve its original dimensions.</summary>
    public double? ReferenceMegapixels { get; init; } = 1;
}

public static class AiPromptDefaults
{
    public const string PreserveAppearance = "Preserve the source image's exposure, white balance, color grading, contrast, sharpness, focus and existing texture. Match the surrounding image naturally. Keep unedited content unchanged.";
}

public static class AiDimensions
{
    public static readonly double[] MegapixelOptions = [0.5, 0.75, 1, 1.5, 2, 3, 4];
    public static readonly int[] ExpansionSides = [768, 1024, 1280, 1536, 1792, 2048];

    public static (int Width, int Height) FromMinimumSide(int minimum, int width, int height, int multiple = 16)
    {
        if (minimum < 0 || width <= 0 || height <= 0 || multiple <= 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!DocumentLimits.FitsSurface(width, height)) throw new InvalidOperationException("Canvas exceeds document limits.");
        if (minimum == 0) return (width, height);
        var scale = (double)minimum / Math.Min(width, height);
        var w = Round(width * scale, multiple); var h = Round(height * scale, multiple);
        if (!DocumentLimits.FitsSurface(w, h)) throw new InvalidOperationException($"Generation exceeds the {DocumentLimits.MaxSide} px / {DocumentLimits.MaxSurfaceMegapixels} MP limit.");
        return (w, h);
    }

    /// <summary>Fits a pixel budget to an aspect ratio while keeping dimensions friendly to latent-image pipelines.</summary>
    public static (int Width, int Height) FromMegapixels(double megapixels, int aspectWidth, int aspectHeight, int multiple = 16)
    {
        if (aspectWidth <= 0 || aspectHeight <= 0) throw new ArgumentOutOfRangeException(nameof(aspectWidth));
        megapixels = Math.Clamp(megapixels, MegapixelOptions[0], MegapixelOptions[^1]);
        multiple = Math.Max(1, multiple);
        var pixels = megapixels * 1_000_000;
        var aspect = (double)aspectWidth / aspectHeight;
        int width, height;
        if (aspect >= 1)
        {
            height = Round(Math.Sqrt(pixels / aspect), multiple);
            width = Round(height * aspect, multiple);
        }
        else
        {
            width = Round(Math.Sqrt(pixels * aspect), multiple);
            height = Round(width / aspect, multiple);
        }
        return (Math.Clamp(width, multiple, DocumentLimits.MaxSide), Math.Clamp(height, multiple, DocumentLimits.MaxSide));
    }

    public static string Label(double megapixels) => $"{megapixels:0.##} MP";

    private static int Round(double value, int multiple) => Math.Max(multiple, (int)Math.Round(value / multiple) * multiple);
}

/// <summary>Canonical editor assets from which an engine binding selects the inputs it needs.</summary>
public sealed class AiTaskInputs : IDisposable
{
    public SKBitmap SourceImage { get; init; } = null!;
    public SKBitmap ContextImage { get; init; } = null!;
    public SKBitmap? ActiveLayerImage { get; init; }
    public SKBitmap? SelectionMask { get; init; }
    /// <summary>The inverse selection, used when a task changes the background while preserving the subject.</summary>
    public SKBitmap? BackgroundMask { get; init; }
    public SKBitmap? AlphaMask { get; init; }
    public SKBitmap? PreprocessedImage { get; init; }
    public SKBitmap? PreprocessedMask { get; init; }
    public SKBitmap? OutputMask { get; init; }
    /// <summary>Conservative support for a workflow's grow and blur; not an additional feather pass.</summary>
    public int TransitionMargin { get; init; }
    public SKBitmap? ReferenceImage { get; init; }
    public IReadOnlyList<SKBitmap> ReferenceImages { get; init; } = [];
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public SKRectI TargetBounds { get; init; }
    /// <summary>Upscale sees a halo around the selection; only TargetBounds is placed back.</summary>
    public SKRectI? UpscaleSourceBounds { get; init; }
    public SKRectI? ExpansionBounds { get; init; }
    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public long Seed { get; init; }

    /// <summary>The real expanded document, not the opaque black conditioning image. Caller owns the copy.</summary>
    public SKBitmap ExpandedContext()
    {
        if (PreprocessedImage == null) throw new InvalidOperationException("No expanded image was prepared.");
        var image = Pixels.NewColor(PreprocessedImage.Width, PreprocessedImage.Height);
        using var canvas = new SKCanvas(image);
        canvas.DrawImage(Pixels.ImageOf(ContextImage), -(ExpansionBounds?.Left ?? 0), -(ExpansionBounds?.Top ?? 0));
        return image;
    }

    public IReadOnlyDictionary<string, SKBitmap> Images()
    {
        var images = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceImage"] = SourceImage,
            ["contextImage"] = ContextImage
        };
        if (ActiveLayerImage != null) images["activeLayerImage"] = ActiveLayerImage;
        if (SelectionMask != null) images["selectionMask"] = SelectionMask;
        if (BackgroundMask != null) images["backgroundMask"] = BackgroundMask;
        if (AlphaMask != null) images["alphaMask"] = AlphaMask;
        if (PreprocessedImage != null) images["preprocessedImage"] = PreprocessedImage;
        if (PreprocessedMask != null) images["preprocessedMask"] = PreprocessedMask;
        if (ReferenceImage != null) images["referenceImage"] = ReferenceImage;
        for (var index = 0; index < ReferenceImages.Count; index++) images[$"referenceImage{index + 1}"] = ReferenceImages[index];
        return images;
    }

    public Dictionary<string, object?> Values(IReadOnlyDictionary<string, string> uploadedImages)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = Prompt,
            ["negativePrompt"] = NegativePrompt,
            ["seed"] = Seed,
            ["width"] = CanvasWidth,
            ["height"] = CanvasHeight
        };
        foreach (var image in uploadedImages) values[image.Key] = image.Value;
        if (ExpansionBounds is { } bounds)
        {
            values["expansionX"] = bounds.Left;
            values["expansionY"] = bounds.Top;
            values["expansionWidth"] = bounds.Width;
            values["expansionHeight"] = bounds.Height;
        }
        values["targetX"] = TargetBounds.Left;
        values["targetY"] = TargetBounds.Top;
        values["targetWidth"] = TargetBounds.Width;
        values["targetHeight"] = TargetBounds.Height;
        return values;
    }

    public void Dispose()
    {
        var disposed = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        foreach (var image in Images().Values) if (disposed.Add(image)) image.Dispose();
        if (OutputMask != null && disposed.Add(OutputMask)) OutputMask.Dispose();
    }
}

public static class AiTaskInputPreparer
{
    public static AiTaskInputs Prepare(EditorSession session, AiTaskRequest request)
    {
        if (request.Task.RequiresSelection() && session.Selection == null)
            throw new InvalidOperationException($"{request.Task.DisplayName()} requires a selection.");
        var expansionSize = request.Task == AiTaskKind.GenerativeExpand
            ? AiDimensions.FromMinimumSide(request.ExpansionMinimumSide, (request.ExpansionBounds ?? session.Document.Bounds).Width, (request.ExpansionBounds ?? session.Document.Bounds).Height)
            : (Width: 0, Height: 0);

        var flattened = session.Flatten();
        var context = Pixels.Clone(flattened);
        var active = RenderActiveLayer(session);
        var selection = session.Selection == null || request.Task is AiTaskKind.ImageEdit or AiTaskKind.GenerateImage ? null : Pixels.Clone(session.Selection);
        var background = request.Task == AiTaskKind.ChangeBackground && selection != null ? Invert(selection) : null;
        var target = request.ExpansionBounds ?? (session.Selection != null && request.Task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.ChangeBackground or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.Upscale
            ? SelectionMask.Bounds(session.Selection) : session.Document.Bounds);
        SKRectI? upscaleBounds = request.Task == AiTaskKind.Upscale && session.Selection != null && !target.IsEmpty
            ? SKRectI.Intersect(session.Document.Bounds,new SKRectI(target.Left-32,target.Top-32,target.Right+32,target.Bottom+32)) : null;
        // Convolution/attention upscalers need surrounding pixels at the patch edge,
        // just as ComfyUI's tiled upscale uses overlap. Do not expose artificial crop borders.
        var source = upscaleBounds is { } area ? Crop(flattened, area) : flattened;
        if (!ReferenceEquals(source, flattened)) flattened.Dispose();
        var alpha = session.ActiveLayer is { Pixels: not null } layer ? SelectionMask.FromLayer(session.Document, layer, fromMask: false) : null;
        SKBitmap? preprocessed = null, preprocessedMask = null;
        if (request.Task == AiTaskKind.RemoveObject && selection != null)
            (preprocessed, preprocessedMask) = RemoveObjectPreprocessor.Prepare(source, selection, request.RemoveObject);
        else if (request.Task == AiTaskKind.ChangeBackground && background != null)
            (preprocessed, preprocessedMask) = RemoveObjectPreprocessor.Prepare(source, background,
                new RemoveObjectSettings { Dilation = 0, Feather = 0 });
        else if (request.Task == AiTaskKind.GenerativeExpand)
            (preprocessed, preprocessedMask) = PrepareExpansion(source, session.Document.Bounds, request.ExpansionBounds ?? session.Document.Bounds,
                request.ExpansionBounds == null ? selection : null);

        var requestedReferences = request.ReferenceImages.Count > 0 ? request.ReferenceImages.Take(6).ToList()
            : request.ReferenceImage == null ? [] : [request.ReferenceImage];
        var references = requestedReferences.Select(image => PrepareReference(image, request.ReferenceMegapixels)).ToList();
        return new AiTaskInputs
        {
            SourceImage = source,
            ContextImage = context,
            ActiveLayerImage = active,
            SelectionMask = selection,
            BackgroundMask = background,
            AlphaMask = alpha,
            PreprocessedImage = preprocessed,
            PreprocessedMask = preprocessedMask,
            OutputMask = request.Task == AiTaskKind.RemoveObject && preprocessedMask != null
                ? RemovalOutputMask(preprocessedMask, request.Settings) : null,
            TransitionMargin = (request.Task == AiTaskKind.GenerativeFill ? 0 : request.Settings.Values.TryGetValue("maskGrow", out var grow) ? Math.Clamp(Convert.ToInt32(grow), 0, 512) : 8)
                + 4 * (request.Settings.Values.TryGetValue("maskBlend", out var blend) ? Math.Clamp(Convert.ToInt32(blend), 0, 512) : 32),
            ReferenceImage = references.FirstOrDefault(),
            ReferenceImages = references,
            CanvasWidth = request.Task == AiTaskKind.GenerativeExpand ? expansionSize.Width
                : request.Settings.Width > 0 ? request.Settings.Width : session.Document.Width,
            CanvasHeight = request.Task == AiTaskKind.GenerativeExpand ? expansionSize.Height
                : request.Settings.Height > 0 ? request.Settings.Height : session.Document.Height,
            TargetBounds = target,
            UpscaleSourceBounds = upscaleBounds,
            ExpansionBounds = request.ExpansionBounds,
            Prompt = string.Join("\n\n", new[] { TaskPrompt(request.Task, request.Prompt, selection != null, request.ExpansionMode),
                request.Task is AiTaskKind.Upscale or AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection
                    ? "" : request.AdditionalPrompt.Trim() }.Where(value => value.Length > 0)),
            NegativePrompt = TaskNegativePrompt(request.Task, request.NegativePrompt),
            Seed = request.Settings.Seed
        };
    }

    private static SKBitmap? RenderActiveLayer(EditorSession session)
    {
        if (session.ActiveLayer is not { Pixels: not null } active) return null;
        var overrides = new Dictionary<Guid, Layer>();
        var keep = new HashSet<Guid> { active.Id };
        for (var parent = session.Document.ParentOf(active.Id); parent != null; parent = session.Document.ParentOf(parent.Id)) keep.Add(parent.Id);
        foreach (var layer in session.Document.AllLayers())
        {
            if (keep.Contains(layer.Id)) continue;
            var hidden = layer.Clone();
            hidden.Visible = false;
            overrides[layer.Id] = hidden;
        }
        return DocumentRenderer.Flatten(session.Document, new RenderOptions { Overrides = overrides });
    }

    private static SKBitmap PrepareReference(SKBitmap source, double? megapixels)
    {
        if (megapixels == null) return Pixels.Clone(source);
        var (width, height) = AiDimensions.FromMegapixels(megapixels.Value, source.Width, source.Height);
        if (width == source.Width && height == source.Height) return Pixels.Clone(source);
        var target = Pixels.NewColor(width, height);
        using var canvas = new SKCanvas(target);
        canvas.DrawImage(Pixels.ImageOf(source), new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return target;
    }

    private static SKBitmap Crop(SKBitmap source, SKRectI bounds)
    {
        var result = Pixels.NewColor(bounds.Width, bounds.Height);
        using var canvas = new SKCanvas(result);
        canvas.DrawImage(Pixels.ImageOf(source), new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            new SKRect(0, 0, bounds.Width, bounds.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return result;
    }

    private static SKBitmap RemovalOutputMask(SKBitmap mask, AiGenerationSettings settings)
    {
        var grow = settings.Values.TryGetValue("maskGrow", out var g) ? Math.Clamp(Convert.ToInt32(g), 0, 512) : 8;
        var blend = settings.Values.TryGetValue("maskBlend", out var b) ? Math.Clamp(Convert.ToInt32(b), 0, 512) : 32;
        // Keep the stitcher's transition outside the original selection; clipping it back to
        // that selection restores object fringes and creates a hard, visible boundary.
        using var expanded = SelectionMask.Expand(mask, grow + blend);
        return blend > 0 ? SelectionMask.Feather(expanded, blend) : Pixels.Clone(expanded);
    }

    private static string RemovePrompt(string guidance)
    {
        const string instruction = "Remove the black patch from this image and reconstruct ONLY the empty background behind it, using the surrounding content as context. The black area is missing background, NOT an object to preserve or redraw. Erase the original foreground subject and its silhouette completely. Do not retain, recreate or introduce a person, animal, object, dark shape, ghost or outline in this area. Continue the surrounding background, lines and patterns through the missing region naturally. Match exposure, white balance, color, focus, sharpness, texture, grain and noise. Leave no black or gray patch and no new foreground subject.";
        return string.IsNullOrWhiteSpace(guidance) ? instruction : $"{instruction} Additional guidance: {guidance.Trim()}";
    }

    private static string TaskPrompt(AiTaskKind task, string prompt, bool selected, AiExpansionMode expansionMode)
    {
        var guidance = prompt.Trim();
        return task switch
        {
            AiTaskKind.RemoveObject => selected ? RemovePrompt(guidance) : $"Remove the requested object and reconstruct its background naturally, preserving unrelated content. Request: {guidance}",
            AiTaskKind.GenerativeFill => $"Create the requested content only inside the masked area. Keep the entire requested object fully visible inside the mask with a clear margin; do not crop or cut off any part of it. Blend lighting, perspective, focus, texture and grain with the surrounding image. Request: {guidance}",
            AiTaskKind.ChangeBackground => selected ? $"Generate a photographic background scene for a composite. Render only the requested environment, without a foreground subject: the original subject will be composited separately. Do not add a person, animal, duplicate subject, cutout, black patch or silhouette unless explicitly requested. Background scene: {guidance}"
                : $"Replace the background of image 1 while keeping its foreground subject's identity, shape and details. Background request: {guidance}",
            AiTaskKind.Harmonize => $"Harmonize {(selected ? "only the masked object" : "the image")} with its surrounding scene while preserving identity, silhouette, geometry, pose and important texture. Match scene lighting direction, exposure, white balance, color, contrast, focus and grain. Guidance: {guidance}",
            AiTaskKind.Relight => $"Relight {(selected ? "only the masked subject" : "the image")}. Preserve identity, geometry, pose, materials and texture. Apply coherent light direction, shadow softness, exposure and color spill. Lighting request: {guidance}",
            AiTaskKind.GenerativeExpand => expansionMode == AiExpansionMode.WholeImage
                ? $"Regenerate the entire expanded image coherently, using image 1 as the composition reference. Fill black empty canvas with a natural continuation. Request: {guidance}"
                : $"Fill ONLY the masked empty canvas; black empty space is missing image, not a black object. Preserve existing image content. Continue perspective, structures, lighting, focus and texture without a visible seam. Request: {guidance}",
            _ => guidance
        };
    }

    private static string TaskNegativePrompt(AiTaskKind task, string negative)
    {
        var required = task == AiTaskKind.GenerativeFill
            ? "cropped object, cut off object, object outside mask, incomplete subject, visible seam"
            : task is AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand
                ? "visible seam, color mismatch, smooth patch, blurry patch, mismatched grain, mismatched noise"
                : "";
        return string.Join(", ", new[] { negative.Trim(), required }.Where(value => value.Length > 0));
    }

    private static SKBitmap Invert(SKBitmap mask)
    {
        var result = Pixels.NewMask(mask.Width, mask.Height);
        var source = mask.GetPixelSpan();
        var target = result.GetPixelSpan();
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
                target[y * result.RowBytes + x] = (byte)(255 - source[y * mask.RowBytes + x]);
        Pixels.Invalidate(result);
        return result;
    }

    private static (SKBitmap Image, SKBitmap Mask) PrepareExpansion(SKBitmap source, SKRectI document, SKRectI expansion, SKBitmap? selection)
    {
        if (expansion.Width <= 0 || expansion.Height <= 0) throw new ArgumentOutOfRangeException(nameof(expansion));
        if (!DocumentLimits.FitsSurface(expansion.Width, expansion.Height)) throw new InvalidOperationException("Expanded canvas exceeds document limits.");
        var image = Pixels.NewColor(expansion.Width, expansion.Height);
        image.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(image)) canvas.DrawImage(Pixels.ImageOf(source), -expansion.Left, -expansion.Top);
        var mask = Pixels.NewMask(expansion.Width, expansion.Height, 255);
        var values = mask.GetPixelSpan();
        for (var y = 0; y < mask.Height; y++)
        for (var x = 0; x < mask.Width; x++)
        {
            var sx = x + expansion.Left; var sy = y + expansion.Top;
            var empty = document.Contains(sx, sy) ? 255 - source.GetPixel(sx, sy).Alpha : 255;
            if (selection != null) empty = empty * selection.GetPixel(sx, sy).Alpha / 255;
            values[y * mask.RowBytes + x] = (byte)empty;
        }
        Pixels.Invalidate(image);
        Pixels.Invalidate(mask);
        return (image, mask);
    }
}

public static class RemoveObjectPreprocessor
{
    public static (SKBitmap Image, SKBitmap Mask) Prepare(SKBitmap source, SKBitmap rawMask, RemoveObjectSettings settings)
    {
        if (source.Width != rawMask.Width || source.Height != rawMask.Height)
            throw new ArgumentException("The source image and remove-object mask must have the same dimensions.");
        var dilation = Math.Clamp(settings.Dilation, 0, 512);
        var feather = Math.Clamp(settings.Feather, 0, 256);
        var expanded = dilation > 0 ? SelectionMask.Expand(rawMask, dilation) : Pixels.Clone(rawMask);
        var mask = feather > 0 ? SelectionMask.Feather(expanded, feather) : Pixels.Clone(expanded);
        var image = Pixels.Clone(source);
        Fill(image, expanded, settings.FillMode == RemoveObjectFillMode.Neutral ? new SKColor(127, 127, 127) : settings.FillColor,
            settings.FillMode == RemoveObjectFillMode.Transparent);
        expanded.Dispose();
        return (image, mask);
    }

    private static unsafe void Fill(SKBitmap image, SKBitmap mask, SKColor color, bool transparent)
    {
        var pixels = (byte*)image.GetPixels();
        var coverage = (byte*)mask.GetPixels();
        for (var y = 0; y < image.Height; y++)
        {
            var row = pixels + (long)y * image.RowBytes;
            var selected = coverage + (long)y * mask.RowBytes;
            for (var x = 0; x < image.Width; x++)
            {
                var amount = selected[x] > 0 ? (byte)255 : (byte)0;
                if (amount == 0) continue;
                var offset = x * 4;
                byte r = transparent ? (byte)0 : color.Red, g = transparent ? (byte)0 : color.Green,
                    b = transparent ? (byte)0 : color.Blue, a = transparent ? (byte)0 : color.Alpha;
                row[offset] = Blend(row[offset], r, amount);
                row[offset + 1] = Blend(row[offset + 1], g, amount);
                row[offset + 2] = Blend(row[offset + 2], b, amount);
                row[offset + 3] = Blend(row[offset + 3], a, amount);
            }
        }
        Pixels.Invalidate(image);
    }

    private static byte Blend(byte from, byte to, byte amount) => (byte)((from * (255 - amount) + to * amount + 127) / 255);
}

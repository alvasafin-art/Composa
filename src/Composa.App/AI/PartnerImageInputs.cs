using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>API inputs use original canvas coordinates. No latent-size crop, model weights or GPU buffers.</summary>
internal sealed class PartnerImageInputs : IDisposable
{
    public Dictionary<string, SKBitmap> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SKBitmap> owned = [];
    private readonly AiTaskInputs original;
    private readonly AiTaskRequest request;
    private readonly SKRectI crop;
    private readonly SKBitmap? blendMask;
    private readonly AiMaskPlan? plan;
    private readonly (int Width, int Height) generationSize;
    internal const int DefaultContextPadding = 0;
    internal const int MaximumContextPadding = 1024;
    internal SKRectI SourceBounds => crop;

    public PartnerImageInputs(AiTaskInputs inputs, AiTaskRequest request)
    {
        original = inputs; this.request = request;
        var source = request.Task is AiTaskKind.RemoveObject or AiTaskKind.ChangeBackground or AiTaskKind.GenerativeExpand
            ? inputs.PreprocessedImage ?? inputs.SourceImage : inputs.SourceImage;
        var mask = request.Task == AiTaskKind.ChangeBackground ? inputs.BackgroundMask
            : request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedMask : inputs.SelectionMask;
        if (request.Task is AiTaskKind.GenerateImage or AiTaskKind.ImageEdit || request.Task == AiTaskKind.GenerativeExpand && request.ExpansionMode == AiExpansionMode.WholeImage) mask = null;
        if (mask != null && SelectionMask.IsEmpty(mask)) throw new InvalidOperationException("The edit mask is empty. For Expand, expose empty canvas or choose Whole image.");
        crop = new SKRectI(0, 0, source.Width, source.Height);
        if (mask != null)
        {
            using var context = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
            var core = request.Task == AiTaskKind.GenerativeExpand ? inputs.OutputMask ?? mask : mask;
            plan = inputs.MaskPlan ?? AutomaticAiMask.Analyze(core, context);
            blendMask = Own(AutomaticAiMask.OutputMask(core, context, plan, request.Task == AiTaskKind.GenerativeExpand));
            if (request.Task == AiTaskKind.RemoveObject)
            {
                var removal = RemoveObjectPreprocessor.Prepare(inputs.SourceImage, mask, new RemoveObjectSettings { Dilation = 0, Feather = 0 });
                source = Own(removal.Image); removal.Mask.Dispose();
            }
            if (request.Task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.GenerativeExpand)
            {
                crop = plan.Bounds;
            }
        }
        if (request.Task != AiTaskKind.GenerateImage) Images["apiSource"] = Own(Crop(source, crop));
        for (var i = 0; i < inputs.ReferenceImages.Count; i++) Images[$"referenceImage{i + 1}"] = inputs.ReferenceImages[i];
        var desired = (Width: crop.Width, Height: crop.Height);
        if (request.Task == AiTaskKind.GenerateImage) desired = (inputs.CanvasWidth, inputs.CanvasHeight);
        else if (request.Task == AiTaskKind.GenerativeExpand) desired = AiDimensions.FromMaximumSide(request.ExpansionMinimumSide, crop.Width, crop.Height);
        else if (!Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false) && request.Settings.Width > 0 && request.Settings.Height > 0)
        {
            var scale = Math.Sqrt((double)request.Settings.Width * request.Settings.Height / crop.Width / crop.Height);
            desired = ((int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale));
        }
        var originalSize = request.Task == AiTaskKind.GenerativeExpand ? request.ExpansionMinimumSide == 0
            : Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false);
        if (originalSize)
        {
            desired = AiDimensions.OriginalGenerationSize(desired.Width, desired.Height);
            if (Images.TryGetValue("apiSource", out var input) && (long)input.Width * input.Height < Composa.Model.DocumentLimits.MinimumGenerationPixels)
                Images["apiSource"] = Own(Resize(input, desired.Width, desired.Height));
        }
        try { generationSize = PartnerImageSize.Plan(desired.Width, desired.Height,
            originalSize ? Composa.Model.DocumentLimits.MinimumGenerationPixels : PartnerImageSize.MinimumPixels); }
        catch { Dispose(); throw; }
    }

    public JsonObject Bind(JsonObject graph, EngineProfile engine, IReadOnlyDictionary<string, string> files, long seed)
    {
        var result = (JsonObject)graph.DeepClone(); var node = result["gpt"]!["inputs"]!.AsObject();
        node["model"] = engine.ApiModel; node["seed"] = seed % int.MaxValue;
        node["model.quality"] = request.Settings.Values.GetValueOrDefault("apiQuality")?.ToString() ?? "low";
        node["model.size"] = "Custom";
        node["model.custom_width"] = generationSize.Width;
        node["model.custom_height"] = generationSize.Height;
        // Masks stay local. Do not let template image ports or upload enumeration order
        // change source/reference numbering, or accidentally add a mask as a reference.
        foreach (var key in node.Select(pair => pair.Key).Where(key => key == "model.mask" || key.StartsWith("model.images.image_", StringComparison.Ordinal)).ToArray())
            node.Remove(key);
        var semantics = Images.ContainsKey("apiSource") ? new List<string> { "apiSource" } : [];
        semantics.AddRange(Enumerable.Range(1, original.ReferenceImages.Count).Select(index => $"referenceImage{index}"));
        var number = 0;
        foreach (var semantic in semantics)
        {
            var filename = files[semantic];
            var id = "composa_api_image_" + ++number;
            result[id] = Load(filename); node[$"model.images.image_{number}"] = new JsonArray(id, 0);
        }
        node["prompt"] = Prompt();
        return result;
    }

    private string Prompt()
    {
        // GPT edits a rectangular image, not an externally described mask. Keep
        // operation instructions, but never refer to a mask it has not received.
        var guidance = request.Prompt.Trim();
        var instruction = request.Task switch
        {
            AiTaskKind.GenerativeFill when request.BlackEditRegion => AiPromptDefaults.Expand,
            AiTaskKind.GenerativeFill => guidance,
            AiTaskKind.Harmonize when blendMask != null => $"Harmonize the object in image 1 with its surrounding scene while preserving identity, silhouette, geometry, pose and important texture. Match scene lighting direction, exposure, white balance, color, contrast, focus and grain. Guidance: {guidance}",
            AiTaskKind.Relight when blendMask != null => $"Relight image 1. Preserve identity, geometry, pose, materials and texture. Apply coherent light direction, shadow softness, exposure and color spill. Lighting request: {guidance}",
            AiTaskKind.GenerativeExpand => AiPromptDefaults.Expand,
            _ => null
        };
        return instruction == null ? original.Prompt
            : string.Join("\n\n", new[] { instruction, request.AdditionalPrompt.Trim() }.Where(value => value.Length > 0));
    }

    public SKBitmap Finish(SKBitmap generated)
    {
        if (request.Task == AiTaskKind.GenerateImage)
        {
            using (generated) return Resize(generated, original.CanvasWidth, original.CanvasHeight);
        }
        var raw = FinishUnmasked(generated);
        if (blendMask == null || request.Task == AiTaskKind.ChangeBackground) return raw;
        using (raw)
        using (var context = request.Task == AiTaskKind.GenerativeExpand ? original.ExpandedContext() : Pixels.Clone(original.ContextImage))
            return AiResultPostprocessor.Constrain(raw, context, blendMask);
    }
    internal SKBitmap? OutputMask()
    {
        return blendMask == null ? null : Pixels.Clone(blendMask);
    }

    internal SKBitmap FinishUnmasked(SKBitmap generated)
    {
        if (request.Task == AiTaskKind.GenerateImage) return Finish(generated);
        using (generated)
        using (var fitted = Resize(generated, crop.Width, crop.Height))
        {
            var result = request.Task == AiTaskKind.GenerativeExpand ? original.ExpandedContext() : Pixels.Clone(original.ContextImage);
            Blend(result, fitted, null, crop);
            if (blendMask == null) return result;
            using (result)
            using (var context = request.Task == AiTaskKind.GenerativeExpand ? original.ExpandedContext() : Pixels.Clone(original.ContextImage))
                return AiSeamlessFinisher.Match(result, context, blendMask, crop, plan!.SeamWidth,
                    request.Task is not (AiTaskKind.Relight or AiTaskKind.Harmonize or AiTaskKind.ChangeBackground));
        }
    }

    private SKBitmap Own(SKBitmap bitmap) { owned.Add(bitmap); return bitmap; }
    private static JsonObject Load(string filename) => new() { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = filename } };
    private static SKBitmap Crop(SKBitmap source, SKRectI bounds)
    {
        var result = source.ColorType == SKColorType.Alpha8 ? Pixels.NewMask(bounds.Width, bounds.Height) : Pixels.NewColor(bounds.Width, bounds.Height);
        using var canvas = new SKCanvas(result); canvas.DrawImage(Pixels.ImageOf(source), -bounds.Left, -bounds.Top); return result;
    }
    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height) return Pixels.Clone(source);
        var mismatch = Math.Abs((double)source.Width / source.Height / ((double)width / height) - 1);
        if (mismatch > 0.025) throw new InvalidDataException($"GPT returned {source.Width} × {source.Height} with different proportions from {width} × {height}. Refusing to stretch or misalign it with the mask; no edit was applied.");
        var result = Pixels.NewColor(width, height); using var canvas = new SKCanvas(result);
        var scale = Math.Max((double)width / source.Width, (double)height / source.Height);
        var w = (float)(source.Width * scale); var h = (float)(source.Height * scale);
        canvas.DrawImage(Pixels.ImageOf(source), new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), new SKSamplingOptions(SKCubicResampler.Mitchell)); return result;
    }
    private static unsafe void Blend(SKBitmap destination, SKBitmap image, SKBitmap? mask, SKRectI bounds)
    {
        var target = (byte*)destination.GetPixels(); var source = (byte*)image.GetPixels(); var coverage = mask == null ? null : (byte*)mask.GetPixels();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            var amount = coverage == null ? 255 : coverage[(long)y * mask!.RowBytes + x];
            for (var c = 0; c < 4; c++)
            {
                var offset = (long)y * destination.RowBytes + x * 4 + c;
                target[offset] = (byte)((target[offset] * (255 - amount) + source[(long)(y - bounds.Top) * image.RowBytes + (x - bounds.Left) * 4 + c] * amount + 127) / 255);
            }
        }
        Pixels.Invalidate(destination);
    }
    public void Dispose() { foreach (var bitmap in owned) bitmap.Dispose(); }
}
